namespace EventWOS.Domain.Enums;

/// <summary>
/// Drives claim order in the worker's queue query, so a cancelled-event alert
/// overtakes a backlog of routine assignment messages instead of queueing
/// behind them. Ordered so that a plain ascending sort is priority order.
/// </summary>
public enum NotificationPriority
{
    /// <summary>Security and safety. Must go out ahead of everything else.</summary>
    Critical = 0,

    /// <summary>Time-critical operations: event cancelled, shift changed today.</summary>
    High = 1,

    /// <summary>Normal business flow: assignments, approvals, invitations.</summary>
    Normal = 2,

    /// <summary>Nice-to-have information. First to be starved under load.</summary>
    Low = 3
}
