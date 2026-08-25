using EventOpsOracle.Application.Interfaces;
using EventOpsOracle.Application.Notifications.Abstractions;
using EventOpsOracle.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EventOpsOracle.Persistence.Notifications;

/// <summary>
/// The push sender's window onto the database: live endpoints in, outcomes out.
///
/// It lives in the persistence layer rather than inside the sender because
/// channel senders are meant to be transport-only. Push is the one channel that
/// has to read state before it can address anybody, and this is where that
/// reading belongs.
/// </summary>
public sealed class PushRegistrationStore : IPushRegistrationStore
{
    private readonly IAppDbContext _db;
    private readonly ILogger<PushRegistrationStore> _logger;

    public PushRegistrationStore(IAppDbContext db, ILogger<PushRegistrationStore> logger)
    {
        _db     = db;
        _logger = logger;
    }

    public async Task<IReadOnlyList<PushEndpoint>> GetActiveEndpointsAsync(Guid userId, CancellationToken ct = default)
    {
        // Ordered newest-first so the device the user most recently proved is
        // live is addressed first. Capped because a runaway client could
        // otherwise turn one notification into hundreds of HTTP calls.
        var rows = await _db.DeviceRegistrations
            .AsNoTracking()
            .Where(d => d.UserId == userId && d.IsActive)
            .OrderByDescending(d => d.LastSeenAt)
            .Take(20)
            .Select(d => new
            {
                d.Id, d.Provider, d.Endpoint, d.P256dhKey, d.AuthSecret, d.PushToken
            })
            .ToListAsync(ct);

        return rows
            .Select(r => new PushEndpoint(r.Id, r.Provider, r.Endpoint, r.P256dhKey, r.AuthSecret, r.PushToken))
            .ToList();
    }

    public Task<int> GetUnreadCountAsync(Guid userId, CancellationToken ct = default)
        // The same count the bell shows: unread, not undelivered. Cancelled and
        // failed notifications still count as unread news if the row exists,
        // because the recipient can still read them in the inbox.
        => _db.Notifications
            .AsNoTracking()
            .Where(n => n.RecipientUserId == userId && n.ReadAt == null)
            .CountAsync(ct);

    public async Task ApplyOutcomesAsync(
        IReadOnlyCollection<PushEndpointOutcome> outcomes, CancellationToken ct = default)
    {
        if (outcomes.Count == 0) return;

        var ids  = outcomes.Select(o => o.RegistrationId).Distinct().ToList();
        var rows = await _db.DeviceRegistrations
            .Where(d => ids.Contains(d.Id))
            .ToDictionaryAsync(d => d.Id, ct);

        var now = DateTime.UtcNow;

        foreach (var outcome in outcomes)
        {
            if (!rows.TryGetValue(outcome.RegistrationId, out var registration)) continue;

            switch (outcome.Outcome)
            {
                case PushSendOutcome.Accepted:
                    registration.RecordSuccess(now);
                    break;

                case PushSendOutcome.EndpointGone:
                    // The push service says this subscription no longer exists.
                    // Retiring it here is what stops the next attempt walking
                    // into the same dead endpoint.
                    registration.Deactivate(outcome.Detail ?? "Push service reported the subscription gone", now);
                    _logger.LogInformation(
                        "Deactivated push registration {RegistrationId}: {Reason}",
                        registration.Id, outcome.Detail ?? "gone");
                    break;

                case PushSendOutcome.TransientFailure:
                    registration.RecordTransientFailure(now);
                    break;

                default:
                    // Permanent, but not a dead endpoint -- our payload or our
                    // credentials. The subscription stays; the bug is ours.
                    registration.RecordTransientFailure(now);
                    break;
            }
        }

        await _db.SaveChangesAsync(ct);
    }
}
