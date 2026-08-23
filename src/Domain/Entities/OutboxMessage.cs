using EventWOS.Domain.Common;
using EventWOS.Domain.Enums;

namespace EventWOS.Domain.Entities;

/// <summary>
/// Transactional outbox row. Business handlers write this in the SAME
/// SaveChanges as the business data, so the two cannot disagree: if the
/// assignment is committed, the notification work is committed with it, and if
/// the transaction rolls back, no message escapes.
///
/// The alternative -- calling providers from the handler -- fails in both
/// directions: a provider outage would roll back a perfectly good assignment,
/// and a crash after commit would silently lose the notification. Here the
/// worker picks the row up whenever it comes back.
///
/// Claimed with FOR UPDATE SKIP LOCKED, so several API instances can process
/// the outbox at once without ever handing the same row to two workers.
/// </summary>
public sealed class OutboxMessage : BaseEntity
{
    private OutboxMessage() { }

    public OutboxMessage(
        string aggregateType,
        Guid? aggregateId,
        string messageType,
        string payloadJson,
        string? correlationId = null,
        DateTime? availableAt = null)
    {
        if (string.IsNullOrWhiteSpace(aggregateType))
            throw new ArgumentException("AggregateType is required.", nameof(aggregateType));
        if (string.IsNullOrWhiteSpace(messageType))
            throw new ArgumentException("MessageType is required.", nameof(messageType));
        if (string.IsNullOrWhiteSpace(payloadJson))
            throw new ArgumentException("Payload is required.", nameof(payloadJson));

        AggregateType = aggregateType;
        AggregateId   = aggregateId;
        MessageType   = messageType;
        PayloadJson   = payloadJson;
        CorrelationId = correlationId;
        Status        = OutboxStatus.Pending;
        AvailableAt   = availableAt ?? DateTime.UtcNow;
    }

    /// <summary>What produced this, e.g. "EventAssignment". Diagnostics only.</summary>
    public string AggregateType { get; private set; } = default!;

    public Guid? AggregateId { get; private set; }

    /// <summary>Handler discriminator, e.g. "NotificationRequested".</summary>
    public string MessageType { get; private set; } = default!;

    /// <summary>JSONB payload the worker needs to expand this into deliveries.</summary>
    public string PayloadJson { get; private set; } = default!;

    public OutboxStatus Status { get; private set; }
    public int AttemptCount { get; private set; }

    /// <summary>Earliest time a worker may claim it. Also the retry backoff mechanism.</summary>
    public DateTime AvailableAt { get; private set; }

    public DateTime? LockedAt { get; private set; }
    public string? LockedBy { get; private set; }
    public DateTime? ProcessedAt { get; private set; }
    public string? LastError { get; private set; }
    public string? CorrelationId { get; private set; }

    public void MarkProcessing(string workerId, DateTime nowUtc)
    {
        Status        = OutboxStatus.Processing;
        LockedBy      = workerId;
        LockedAt      = nowUtc;
        AttemptCount += 1;
        UpdatedAt     = nowUtc;
    }

    public void MarkProcessed(DateTime nowUtc)
    {
        Status      = OutboxStatus.Processed;
        ProcessedAt = nowUtc;
        LockedBy    = null;
        LockedAt    = null;
        LastError   = null;
        UpdatedAt   = nowUtc;
    }

    /// <summary>Transient problem: back off and let another pass pick it up.</summary>
    public void ScheduleRetry(string error, DateTime availableAt, DateTime nowUtc)
    {
        Status      = OutboxStatus.Pending;
        LastError   = Truncate(error);
        AvailableAt = availableAt;
        LockedBy    = null;
        LockedAt    = null;
        UpdatedAt   = nowUtc;
    }

    /// <summary>Out of attempts. Kept for inspection and manual replay -- an unprocessed outbox row means someone was never told something.</summary>
    public void MarkFailed(string error, DateTime nowUtc)
    {
        Status    = OutboxStatus.Failed;
        LastError = Truncate(error);
        LockedBy  = null;
        LockedAt  = null;
        UpdatedAt = nowUtc;
    }

    /// <summary>Recovers a row whose worker crashed while holding it.</summary>
    public void ReleaseStaleLock(DateTime nowUtc)
    {
        if (Status != OutboxStatus.Processing) return;

        Status      = OutboxStatus.Pending;
        AvailableAt = nowUtc;
        LockedBy    = null;
        LockedAt    = null;
        UpdatedAt   = nowUtc;
    }

    private static string Truncate(string? error)
        => string.IsNullOrWhiteSpace(error) ? "Unknown error"
         : error.Length <= 1000 ? error : error[..1000];
}
