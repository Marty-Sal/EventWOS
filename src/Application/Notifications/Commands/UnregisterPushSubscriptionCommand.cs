using EventWOS.Application.Interfaces;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Notifications.Commands;

/// <summary>
/// Turns push off for one device -- the user unticking notifications in settings,
/// or a client noticing the browser has revoked its own subscription.
///
/// Deactivates rather than deletes. The row is the record of a device having been
/// registered, and a "why did they stop getting notifications" question is only
/// answerable if the answer is still there. It also means re-enabling reuses the
/// same row instead of racing the unique index on the endpoint.
///
/// Scoped to the caller: a client that guessed another endpoint must not be able
/// to silence someone else's phone.
/// </summary>
/// <param name="Endpoint">
/// The browser's endpoint. Preferred, because that is what a client always knows
/// about itself without having stored our id.
/// </param>
/// <param name="RegistrationId">
/// Alternative, for a settings screen removing a device from a list.
/// </param>
public sealed record UnregisterPushSubscriptionCommand(
    Guid    UserId,
    string? Endpoint       = null,
    Guid?   RegistrationId = null) : IRequest<Result<int>>;

public sealed class UnregisterPushSubscriptionHandler
    : IRequestHandler<UnregisterPushSubscriptionCommand, Result<int>>
{
    private readonly IAppDbContext _db;
    private readonly IUnitOfWork   _uow;

    public UnregisterPushSubscriptionHandler(IAppDbContext db, IUnitOfWork uow)
    {
        _db  = db;
        _uow = uow;
    }

    public async Task<Result<int>> Handle(UnregisterPushSubscriptionCommand req, CancellationToken ct)
    {
        if (req.UserId == Guid.Empty)
            return Result.Failure<int>(Error.Custom("Push.Validation", "A push subscription needs an authenticated user."));

        var hasEndpoint = !string.IsNullOrWhiteSpace(req.Endpoint);
        if (!hasEndpoint && req.RegistrationId is null)
            return Result.Failure<int>(Error.Custom("Push.Validation", "Provide either the subscription endpoint or the registration id."));

        // UserId is part of the query, not a check after loading: the endpoint
        // arrives from the client, so it is not evidence of ownership.
        var query = _db.DeviceRegistrations.Where(d => d.UserId == req.UserId && d.IsActive);

        if (hasEndpoint)
        {
            var endpoint = req.Endpoint!.Trim();
            query = query.Where(d => d.Endpoint == endpoint);
        }

        if (req.RegistrationId is { } id)
            query = query.Where(d => d.Id == id);

        var rows = await query.ToListAsync(ct);

        // Nothing to do is a success, not an error: a client unsubscribing twice,
        // or unsubscribing a device we already retired on a 410, has got exactly
        // the outcome it asked for.
        if (rows.Count == 0) return Result.Success(0);

        var now = DateTime.UtcNow;
        foreach (var row in rows)
            row.Deactivate("Unregistered by the user", now);

        await _uow.SaveChangesAsync(ct);
        return Result.Success(rows.Count);
    }
}
