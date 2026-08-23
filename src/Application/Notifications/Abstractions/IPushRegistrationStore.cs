using EventWOS.Application.Notifications.Contracts;

namespace EventWOS.Application.Notifications.Abstractions;

/// <summary>
/// The persistence the push sender needs, expressed as intent rather than as a
/// DbContext.
///
/// Channel senders are supposed to be transport-only -- no database, no retry
/// decisions -- and push is the one channel that genuinely needs to read state
/// before it can address anybody. Rather than hand the sender an IAppDbContext
/// and quietly break that rule, the two things it needs are named here and
/// implemented in the persistence layer.
/// </summary>
public interface IPushRegistrationStore
{
    /// <summary>Every live endpoint for this recipient. Empty is normal: most users never enable push.</summary>
    Task<IReadOnlyList<PushEndpoint>> GetActiveEndpointsAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// The recipient's authoritative unread count, for the badge. Read from the
    /// notification table, never inferred from how many pushes were sent, so the
    /// number is the same one the bell shows.
    /// </summary>
    Task<int> GetUnreadCountAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Records what happened to each endpoint in one round trip: successes,
    /// transient failures, and the dead subscriptions to retire.
    /// </summary>
    Task ApplyOutcomesAsync(IReadOnlyCollection<PushEndpointOutcome> outcomes, CancellationToken ct = default);
}

/// <param name="RegistrationId">Which registration this refers to.</param>
/// <param name="Outcome">What the push service said.</param>
/// <param name="Detail">Short reason, stored only when a registration is retired.</param>
public sealed record PushEndpointOutcome(Guid RegistrationId, PushSendOutcome Outcome, string? Detail = null);
