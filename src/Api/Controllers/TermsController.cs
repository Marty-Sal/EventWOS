using EventOpsOracle.Api.Authorization;
using Asp.Versioning;
using EventOpsOracle.Application.Terms.Commands;
using EventOpsOracle.Application.Terms.Queries;
using EventOpsOracle.Domain.Enums;
using EventOpsOracle.Domain.Interfaces;
using EventOpsOracle.Shared.Common;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EventOpsOracle.Api.Controllers;

/// <summary>
/// Settings → Terms &amp; Conditions, plus the endpoints self-registration
/// and the post-login gate need. Two audiences: Vendor and Crew — each
/// gets its own document, versioned independently.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/terms")]
[Produces("application/json")]
public sealed class TermsController : ControllerBase
{
    private readonly IMediator    _mediator;
    private readonly ICurrentUser _currentUser;

    public TermsController(IMediator mediator, ICurrentUser currentUser)
    {
        _mediator    = mediator;
        _currentUser = currentUser;
    }

    /// <summary>Public — self-registration pages need this before the user has an account.</summary>
    [AllowAnonymous]
    [HttpGet("current")]
    public async Task<IActionResult> GetCurrent([FromQuery] TermsAudience audience, CancellationToken ct)
    {
        var res = await _mediator.Send(new GetCurrentTermsQuery(audience), ct);
        return res.IsSuccess
            ? Ok(ApiResponse<object>.Ok(res.Value))
            : BadRequest(ApiResponse<object>.Fail(res.Error.Message));
    }

    /// <summary>The post-login gate check for the current user.</summary>
    [Authorize]
    [HttpGet("me/status")]
    public async Task<IActionResult> MyStatus(CancellationToken ct)
    {
        if (_currentUser.UserId is null || _currentUser.Role is null)
            return Unauthorized(ApiResponse.Fail("Not authenticated."));

        var res = await _mediator.Send(new GetMyTermsStatusQuery(_currentUser.UserId.Value, _currentUser.Role.Value), ct);
        return res.IsSuccess
            ? Ok(ApiResponse<object>.Ok(res.Value))
            : BadRequest(ApiResponse<object>.Fail(res.Error.Message));
    }

    /// <summary>Existing logged-in user (re-)accepts the current version — the post-login modal's Accept button.</summary>
    public sealed record AcceptRequest(TermsAudience Audience, int Version);

    [Authorize]
    [HttpPost("accept")]
    public async Task<IActionResult> Accept([FromBody] AcceptRequest req, CancellationToken ct)
    {
        if (_currentUser.UserId is null)
            return Unauthorized(ApiResponse.Fail("Not authenticated."));

        var res = await _mediator.Send(new AcceptTermsCommand(_currentUser.UserId.Value, req.Audience, req.Version), ct);
        return res.IsSuccess ? Ok(ApiResponse.Ok("Accepted.")) : BadRequest(ApiResponse.Fail(res.Error.Message));
    }

    /// <summary>Admin publishes a new version — Settings → Terms &amp; Conditions editor's Save button.</summary>
    public sealed record UpsertRequest(TermsAudience Audience, string Content);

    [Permission("terms:write")]
    [HttpPost]
    public async Task<IActionResult> Upsert([FromBody] UpsertRequest req, CancellationToken ct)
    {
        var res = await _mediator.Send(new UpsertTermsCommand(req.Audience, req.Content, _currentUser.UserId!.Value), ct);
        return res.IsSuccess
            ? Ok(ApiResponse<object>.Ok(res.Value))
            : BadRequest(ApiResponse<object>.Fail(res.Error.Message));
    }
}
