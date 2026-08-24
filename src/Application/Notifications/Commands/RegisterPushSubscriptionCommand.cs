using EventWOS.Application.Interfaces;
using EventWOS.Domain.Entities;
using EventWOS.Domain.Enums;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EventWOS.Application.Notifications.Commands;

/// <summary>
/// Records a browser's Web Push subscription so this user's devices can be
/// pushed to. Called after the browser grants permission, and again on every
/// subsequent visit -- browsers rotate subscriptions on their own schedule, so a
/// client that only registers once eventually goes silent without anyone
/// noticing.
///
/// The endpoint is the identity of a subscription, which makes this an upsert
/// rather than an insert. Three cases, all of them real:
///
///   * same user, same endpoint -- a heartbeat. Touch it, so a row retired by a
///     404 comes back the moment the browser proves it is reachable again.
///   * same endpoint, DIFFERENT user -- a shared phone. The endpoint identifies
///     the browser, not the person, so the row is reassigned. Otherwise the
///     previous crew member would keep getting notifications on a device they
///     handed back.
///   * unknown endpoint -- a new registration.
/// </summary>
/// <param name="UserId">Always the authenticated caller. Never accepted from the client.</param>
/// <param name="DeviceId">Client-supplied and advisory only -- never used for authorization.</param>
public sealed record RegisterPushSubscriptionCommand(
    Guid    UserId,
    string  Endpoint,
    string  P256dhKey,
    string  AuthSecret,
    string? DeviceId  = null,
    string? Platform  = null,
    string? UserAgent = null) : IRequest<Result<PushSubscriptionRegisteredDto>>;

/// <param name="RegistrationId">The stored row, so a client can unsubscribe precisely.</param>
/// <param name="IsNew">False when an existing subscription was refreshed rather than created.</param>
public sealed record PushSubscriptionRegisteredDto(Guid RegistrationId, bool IsNew);

public sealed class RegisterPushSubscriptionHandler
    : IRequestHandler<RegisterPushSubscriptionCommand, Result<PushSubscriptionRegisteredDto>>
{
    /// <summary>
    /// One browser profile per user is normal; a handful is plausible. Well past
    /// that is a client bug looping, and every extra row costs an HTTP request on
    /// every notification -- so the door closes rather than letting one account
    /// quietly become a fan-out amplifier.
    /// </summary>
    public const int MaxActiveDevicesPerUser = 20;

    private readonly IAppDbContext _db;
    private readonly IUnitOfWork   _uow;
    private readonly ILogger<RegisterPushSubscriptionHandler> _logger;

    public RegisterPushSubscriptionHandler(
        IAppDbContext db, IUnitOfWork uow, ILogger<RegisterPushSubscriptionHandler> logger)
    {
        _db     = db;
        _uow    = uow;
        _logger = logger;
    }

    public async Task<Result<PushSubscriptionRegisteredDto>> Handle(
        RegisterPushSubscriptionCommand req, CancellationToken ct)
    {
        var validation = Validate(req);
        if (validation is not null) return Result.Failure<PushSubscriptionRegisteredDto>(validation);

        var endpoint = req.Endpoint.Trim();
        var now      = DateTime.UtcNow;

        // Matched on endpoint alone, deliberately NOT scoped to the caller: the
        // unique index is on the endpoint, so scoping the lookup would find
        // nothing for a shared device and then fail on insert.
        var existing = await _db.DeviceRegistrations
            .FirstOrDefaultAsync(d => d.Endpoint == endpoint, ct);

        if (existing is not null)
        {
            if (existing.UserId != req.UserId)
            {
                _logger.LogInformation(
                    "Push registration {RegistrationId} reassigned to user {UserId} -- same browser, different sign-in",
                    existing.Id, req.UserId);
                existing.ReassignTo(req.UserId, now);
            }

            existing.Touch(now, req.Platform, req.UserAgent, req.DeviceId);

            // Browsers may keep an endpoint and rotate its encryption keys.
            // Sending with stale keys fails permanently, so the new ones win.
            existing.RotateKeys(req.P256dhKey.Trim(), req.AuthSecret.Trim(), now);

            await _uow.SaveChangesAsync(ct);
            return Result.Success(new PushSubscriptionRegisteredDto(existing.Id, IsNew: false));
        }

        var activeCount = await _db.DeviceRegistrations
            .CountAsync(d => d.UserId == req.UserId && d.IsActive, ct);

        if (activeCount >= MaxActiveDevicesPerUser)
            return Result.Failure<PushSubscriptionRegisteredDto>(
                Error.Custom("Push.Validation", $"This account already has {MaxActiveDevicesPerUser} registered devices. Remove one before adding another."));

        var registration = DeviceRegistration.ForWebPush(
            req.UserId, endpoint, req.P256dhKey.Trim(), req.AuthSecret.Trim(),
            now, req.DeviceId, req.Platform, req.UserAgent);

        _db.DeviceRegistrations.Add(registration);
        await _uow.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Push registration created for user {UserId} on {Platform}", req.UserId, registration.Platform ?? "unknown");

        return Result.Success(new PushSubscriptionRegisteredDto(registration.Id, IsNew: true));
    }

    /// <summary>
    /// Shape checks only -- whether the endpoint actually works is something only
    /// a push service can tell us, and it tells us with a 410 later.
    /// </summary>
    public static Error? Validate(RegisterPushSubscriptionCommand req)
    {
        if (req.UserId == Guid.Empty)
            return Error.Custom("Push.Validation", "A push subscription needs an authenticated user.");

        if (string.IsNullOrWhiteSpace(req.Endpoint))
            return Error.Custom("Push.Validation", "The push subscription endpoint is required.");

        var endpoint = req.Endpoint.Trim();

        if (endpoint.Length > DeviceRegistration.MaxEndpointLength)
            return Error.Custom("Push.Validation", "The push subscription endpoint is too long.");

        // Push services are always https. Rejecting anything else keeps a
        // malformed client -- or a probe -- from parking a junk row in the table
        // that we would then try to POST to on every notification.
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return Error.Custom("Push.Validation", "The push subscription endpoint must be an absolute https URL.");

        if (string.IsNullOrWhiteSpace(req.P256dhKey) || string.IsNullOrWhiteSpace(req.AuthSecret))
            return Error.Custom("Push.Validation", "Push encryption keys are required. Without them a push carries no content.");

        // Real values are base64url of a 65-byte point and a 16-byte secret.
        // Loose bounds, since the exact encoding varies by browser.
        if (req.P256dhKey.Trim().Length is < 60 or > 200)
            return Error.Custom("Push.Validation", "The p256dh key looks malformed.");

        if (req.AuthSecret.Trim().Length is < 16 or > 100)
            return Error.Custom("Push.Validation", "The auth secret looks malformed.");

        return null;
    }
}
