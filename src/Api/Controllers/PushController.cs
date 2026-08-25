using Asp.Versioning;
using EventOpsOracle.Application.Notifications.Commands;
using EventOpsOracle.Application.Notifications.Queries;
using EventOpsOracle.Domain.Interfaces;
using EventOpsOracle.Infrastructure.Notifications.Channels;
using EventOpsOracle.Shared.Common;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace EventOpsOracle.Api.Controllers;

/// <summary>
/// Web Push subscription management for the calling user's own devices.
///
/// Like the inbox controller, this is authenticated but NOT permission-gated:
/// crew and vendors hold no notifications:* permission, and every route is scoped
/// to the caller's own id. No route accepts a user id from the client, because
/// that would let anyone register a device against someone else's account and
/// read their notifications from a browser they control.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/push")]
[Authorize]
[Produces("application/json")]
public sealed class PushController : ControllerBase
{
    private readonly IMediator    _mediator;
    private readonly ICurrentUser _currentUser;
    private readonly WebPushOptions _webPush;

    public PushController(IMediator mediator, ICurrentUser currentUser, IOptions<WebPushOptions> webPush)
    {
        _mediator    = mediator;
        _currentUser = currentUser;
        _webPush     = webPush.Value;
    }

    /// <summary>
    /// What the browser needs before it can subscribe: whether push is switched
    /// on, and the VAPID application server public key.
    ///
    /// Returning the public key is correct and not a leak -- the browser passes it
    /// to pushManager.subscribe, so it is public by design. The private key is
    /// never exposed by any route.
    /// </summary>
    [HttpGet("config")]
    [ProducesResponseType(typeof(ApiResponse<PushConfigDto>), 200)]
    public IActionResult Config()
    {
        var enabled = _webPush.Enabled && _webPush.HasKeys;

        return Ok(ApiResponse<PushConfigDto>.Ok(new PushConfigDto(
            Enabled:            enabled,
            PublicKey:          enabled ? _webPush.PublicKey : null,
            // Told to the client so the UI can explain itself honestly rather
            // than showing a toggle that silently does nothing on iOS.
            RequiresHomeScreenOnIos: true)));
    }

    /// <summary>Registers or refreshes this browser's subscription. Safe to call on every visit.</summary>
    [HttpPost("subscribe")]
    [ProducesResponseType(typeof(ApiResponse<PushSubscriptionRegisteredDto>), 200)]
    [ProducesResponseType(typeof(ApiResponse), 400)]
    public async Task<IActionResult> Subscribe([FromBody] PushSubscribeRequest request, CancellationToken ct)
    {
        if (request is null)
            return BadRequest(ApiResponse.Fail("A subscription payload is required."));

        var result = await _mediator.Send(new RegisterPushSubscriptionCommand(
            UserId:    _currentUser.UserId!.Value,
            Endpoint:  request.Endpoint ?? string.Empty,
            P256dhKey: request.P256dh   ?? string.Empty,
            AuthSecret: request.Auth    ?? string.Empty,
            DeviceId:  request.DeviceId,
            Platform:  request.Platform,
            // Read from the request rather than the body: a client cannot be
            // trusted to describe itself, and the header is already there.
            UserAgent: Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null), ct);

        if (result.IsFailure)
            return BadRequest(ApiResponse.Fail(result.Error.Message));

        return Ok(ApiResponse<PushSubscriptionRegisteredDto>.Ok(result.Value));
    }

    /// <summary>
    /// Turns push off for one device. Idempotent -- unsubscribing something already
    /// retired is a success, because the caller has the outcome it asked for.
    /// </summary>
    [HttpPost("unsubscribe")]
    [ProducesResponseType(typeof(ApiResponse<int>), 200)]
    public async Task<IActionResult> Unsubscribe([FromBody] PushUnsubscribeRequest request, CancellationToken ct)
    {
        var result = await _mediator.Send(new UnregisterPushSubscriptionCommand(
            _currentUser.UserId!.Value, request?.Endpoint, request?.RegistrationId), ct);

        if (result.IsFailure)
            return BadRequest(ApiResponse.Fail(result.Error.Message));

        return Ok(ApiResponse<int>.Ok(result.Value, $"{result.Value} device(s) unsubscribed."));
    }

    /// <summary>The caller's registered devices, for a settings screen. Never returns keys or endpoints.</summary>
    [HttpGet("devices")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<PushDeviceDto>>), 200)]
    public async Task<IActionResult> Devices(CancellationToken ct)
    {
        var result = await _mediator.Send(new GetMyPushDevicesQuery(_currentUser.UserId!.Value), ct);

        if (result.IsFailure)
            return BadRequest(ApiResponse.Fail(result.Error.Message));

        return Ok(ApiResponse<IReadOnlyList<PushDeviceDto>>.Ok(result.Value));
    }
}

/// <param name="RequiresHomeScreenOnIos">
/// iOS only delivers Web Push to a PWA that has been added to the home screen.
/// Surfaced so the UI can say so instead of leaving an iPhone user wondering.
/// </param>
public sealed record PushConfigDto(bool Enabled, string? PublicKey, bool RequiresHomeScreenOnIos);

/// <summary>
/// Mirrors the browser's PushSubscription.toJSON(): endpoint plus the two keys.
/// No user id -- the token decides who this belongs to.
/// </summary>
public sealed record PushSubscribeRequest(
    string? Endpoint,
    string? P256dh,
    string? Auth,
    string? DeviceId = null,
    string? Platform = null);

public sealed record PushUnsubscribeRequest(string? Endpoint, Guid? RegistrationId);
