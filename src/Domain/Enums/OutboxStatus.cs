namespace EventOpsOracle.Domain.Enums;

/// <summary>State of a transactional-outbox message.</summary>
public enum OutboxStatus
{
    /// <summary>Committed with the business transaction, not yet processed.</summary>
    Pending = 0,

    /// <summary>Claimed by a worker (row locked, LockedAt stamped for crash recovery).</summary>
    Processing = 1,

    /// <summary>Successfully expanded into notification deliveries.</summary>
    Processed = 2,

    /// <summary>Gave up after the configured attempts. Kept for inspection, never deleted.</summary>
    Failed = 3
}
