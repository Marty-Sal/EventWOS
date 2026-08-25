using EventOpsOracle.Domain.Entities;

namespace EventOpsOracle.Application.Notifications.Abstractions;

/// <summary>
/// Claims work from the notification tables. Implemented in Infrastructure
/// because the claim is raw SQL: <c>SELECT ... FOR UPDATE SKIP LOCKED</c>.
///
/// SKIP LOCKED is the whole reason no broker is needed here. Several API
/// instances can poll the same tables concurrently and Postgres hands each row
/// to exactly one of them, skipping rows another worker already holds instead of
/// blocking behind them. Without it, two replicas would either serialise on the
/// same rows or double-send.
///
/// Every claim runs inside its own transaction and returns rows already flipped
/// to Processing and stamped with the worker id, so a crash leaves an obvious
/// trail (locked_by / locked_at) that the stale-lock sweep can recover.
/// </summary>
public interface INotificationWorkQueue
{
    /// <summary>Claims due outbox messages -- oldest available first.</summary>
    Task<IReadOnlyList<OutboxMessage>> ClaimOutboxBatchAsync(
        string workerId, int batchSize, CancellationToken ct = default);

    /// <summary>
    /// Claims due deliveries, best priority first. In-app deliveries are cheap
    /// and external ones are not, but ordering is by priority alone: a Critical
    /// event cancellation must not queue behind a backlog of routine messages.
    /// </summary>
    Task<IReadOnlyList<NotificationDelivery>> ClaimDeliveryBatchAsync(
        string workerId, int batchSize, CancellationToken ct = default);

    /// <summary>
    /// Returns rows whose worker died while holding them (Processing past the
    /// lock timeout) to Pending. Without this a single container kill would
    /// strand those notifications forever, which is exactly the silent failure
    /// this subsystem exists to prevent.
    /// </summary>
    Task<int> ReleaseStaleLocksAsync(TimeSpan lockTimeout, CancellationToken ct = default);

    /// <summary>Persists changes made to claimed entities.</summary>
    Task SaveChangesAsync(CancellationToken ct = default);
}
