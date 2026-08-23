using EventWOS.Application.Notifications.Abstractions;
using EventWOS.Domain.Entities;
using EventWOS.Domain.Enums;
using EventWOS.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EventWOS.Infrastructure.Notifications;

/// <summary>
/// Postgres-backed work queue. The claim is raw SQL because
/// <c>FOR UPDATE SKIP LOCKED</c> has no LINQ equivalent, and it is the single
/// most important line in the subsystem:
///
///   SELECT ... WHERE due ORDER BY ... FOR UPDATE SKIP LOCKED
///
/// It gives each concurrent worker a disjoint set of rows. Rows another worker
/// already holds are skipped rather than waited on, so N API replicas process N
/// times as fast with no coordinator, no lease table and no broker -- and
/// crucially, no row is ever handed to two workers, which would mean sending the
/// same WhatsApp message twice.
///
/// Claim and mark-as-Processing happen in ONE transaction. If this process dies
/// immediately afterwards, the rows stay Processing with locked_at set, and
/// <see cref="ReleaseStaleLocksAsync"/> recovers them.
/// </summary>
public sealed class NotificationWorkQueue : INotificationWorkQueue
{
    private readonly AppDbContext _db;
    private readonly ILogger<NotificationWorkQueue> _logger;

    public NotificationWorkQueue(AppDbContext db, ILogger<NotificationWorkQueue> logger)
    {
        _db     = db;
        _logger = logger;
    }

    public async Task<IReadOnlyList<OutboxMessage>> ClaimOutboxBatchAsync(
        string workerId, int batchSize, CancellationToken ct = default)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        // Oldest first: notification order should follow the order the business
        // events actually happened in.
        var claimed = await _db.OutboxMessages
            .FromSqlRaw(
                """
                SELECT * FROM outbox_messages
                 WHERE status = 'Pending'
                   AND available_at <= now()
                   AND is_deleted = false
                 ORDER BY available_at
                 LIMIT {0}
                 FOR UPDATE SKIP LOCKED
                """.Replace("{0}", batchSize.ToString()))
            .ToListAsync(ct);

        if (claimed.Count == 0)
        {
            await tx.CommitAsync(ct);
            return Array.Empty<OutboxMessage>();
        }

        var now = DateTime.UtcNow;
        foreach (var message in claimed)
            message.MarkProcessing(workerId, now);

        // Committing the Processing flip while still holding the row locks is
        // what makes the claim exclusive: another worker polling a millisecond
        // later sees status != 'Pending' and moves on.
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        _logger.LogDebug("Worker {WorkerId} claimed {Count} outbox message(s)", workerId, claimed.Count);
        return claimed;
    }

    public async Task<IReadOnlyList<NotificationDelivery>> ClaimDeliveryBatchAsync(
        string workerId, int batchSize, CancellationToken ct = default)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        // Priority is stored as its enum NAME, so ordering has to be explicit --
        // alphabetically 'Critical' < 'High' < 'Low' < 'Normal', which would put
        // Low ahead of Normal. Readability of the column is worth this CASE.
        var claimed = await _db.NotificationDeliveries
            .FromSqlRaw(
                """
                SELECT * FROM notification_deliveries
                 WHERE status = 'Pending'
                   AND next_attempt_at <= now()
                   AND is_deleted = false
                 ORDER BY CASE priority
                            WHEN 'Critical' THEN 0
                            WHEN 'High'     THEN 1
                            WHEN 'Normal'   THEN 2
                            ELSE 3
                          END,
                          next_attempt_at
                 LIMIT {0}
                 FOR UPDATE SKIP LOCKED
                """.Replace("{0}", batchSize.ToString()))
            .ToListAsync(ct);

        if (claimed.Count == 0)
        {
            await tx.CommitAsync(ct);
            return Array.Empty<NotificationDelivery>();
        }

        var now = DateTime.UtcNow;
        foreach (var delivery in claimed)
            delivery.MarkProcessing(workerId, now);

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        _logger.LogDebug("Worker {WorkerId} claimed {Count} delivery row(s)", workerId, claimed.Count);
        return claimed;
    }

    public async Task<int> ReleaseStaleLocksAsync(TimeSpan lockTimeout, CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow - lockTimeout;

        // Set-based UPDATEs rather than loading entities: this is a recovery
        // sweep that should cost nothing when there is nothing to recover.
        // next_attempt_at is reset to now() so recovered work runs immediately.
        var deliveries = await _db.Database.ExecuteSqlRawAsync(
            """
            UPDATE notification_deliveries
               SET status = 'Pending', locked_by = NULL, locked_at = NULL,
                   next_attempt_at = now(), updated_at = now()
             WHERE status = 'Processing' AND locked_at < {0}
            """, new object[] { cutoff }, ct);

        var outbox = await _db.Database.ExecuteSqlRawAsync(
            """
            UPDATE outbox_messages
               SET status = 'Pending', locked_by = NULL, locked_at = NULL,
                   available_at = now(), updated_at = now()
             WHERE status = 'Processing' AND locked_at < {0}
            """, new object[] { cutoff }, ct);

        var total = deliveries + outbox;
        if (total > 0)
        {
            // Always worth a warning: it means a worker died mid-flight, and the
            // count tells an operator how much was stranded.
            _logger.LogWarning(
                "Released {Total} stale notification lock(s) ({Deliveries} deliveries, {Outbox} outbox) older than {LockTimeout}",
                total, deliveries, outbox, lockTimeout);
        }

        return total;
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}
