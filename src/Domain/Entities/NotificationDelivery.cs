using EventWOS.Domain.Common;
using EventWOS.Domain.Enums;

namespace EventWOS.Domain.Entities;

/// <summary>
/// One channel's attempt to deliver a <see cref="Notification"/>: the WhatsApp
/// message, the email, the in-app row. Each carries its own status, attempt
/// count and retry schedule, so channels succeed and fail independently.
///
/// This row is also the audit trail for "why didn't they get it": provider,
/// provider message id, attempt count, last error, and the timestamps of every
/// state it passed through. Failures are never deleted.
/// </summary>
public sealed class NotificationDelivery : BaseEntity
{
    private NotificationDelivery() { }

    internal NotificationDelivery(
        Guid notificationId,
        NotificationChannel channel,
        string? destination,
        string provider,
        int templateVersion,
        NotificationPriority priority)
    {
        NotificationId  = notificationId;
        Channel         = channel;
        Destination     = destination;
        Provider        = provider;
        TemplateVersion = templateVersion;
        Priority        = priority;
        Status          = NotificationStatus.Pending;
        NextAttemptAt   = DateTime.UtcNow;
    }

    public Guid NotificationId { get; private set; }

    public NotificationChannel Channel { get; private set; }

    /// <summary>
    /// Copied from the parent notification rather than joined. The worker claims
    /// work with WHERE status/next_attempt_at ORDER BY priority, and a join to
    /// notifications for that ordering would defeat the covering index on this
    /// table -- the one query that runs constantly under load. Priority never
    /// changes after creation, so the copy cannot drift.
    /// </summary>
    public NotificationPriority Priority { get; private set; }

    /// <summary>
    /// Where it was sent: email address or mobile number, snapshotted at
    /// creation. Kept on the row on purpose -- if a user later changes their
    /// number, history must still show where the message actually went.
    /// </summary>
    public string? Destination { get; private set; }

    /// <summary>Provider that handled this attempt, e.g. "AiSensy", "AmazonSes", "SignalR".</summary>
    public string Provider { get; private set; } = default!;

    /// <summary>Template version used, so a later template edit cannot rewrite history.</summary>
    public int TemplateVersion { get; private set; }

    public NotificationStatus Status { get; private set; }

    /// <summary>The provider's own id for this message. The only reliable way to match an inbound webhook back to this row.</summary>
    public string? ProviderMessageId { get; private set; }

    /// <summary>Opaque provider reference (SES request id, AiSensy submission id) for support escalations. Never the full payload.</summary>
    public string? ProviderResponseReference { get; private set; }

    public int AttemptCount { get; private set; }
    public DateTime? LastAttemptAt { get; private set; }

    /// <summary>When a worker may next try. Null once the row is terminal, which also keeps it out of the queue index.</summary>
    public DateTime? NextAttemptAt { get; private set; }

    public DateTime? AcceptedAt { get; private set; }
    public DateTime? DeliveredAt { get; private set; }
    public DateTime? ReadAt { get; private set; }
    public DateTime? FailedAt { get; private set; }

    /// <summary>Short, human-readable reason shown to admins. Not a stack trace, and never provider credentials.</summary>
    public string? FailureReason { get; private set; }

    /// <summary>Worker instance that claimed this row, for diagnosing stuck deliveries.</summary>
    public string? LockedBy { get; private set; }
    public DateTime? LockedAt { get; private set; }

    /// <summary>Terminal states are never retried and never reopened by a late webhook.</summary>
    public bool IsTerminal => Status is NotificationStatus.Failed or NotificationStatus.Cancelled;

    public void MarkProcessing(string workerId, DateTime nowUtc)
    {
        Status        = NotificationStatus.Processing;
        LockedBy      = workerId;
        LockedAt      = nowUtc;
        AttemptCount += 1;
        LastAttemptAt = nowUtc;
        UpdatedAt     = nowUtc;
    }

    /// <summary>
    /// The provider took the message. Deliberately NOT "sent": an HTTP 200 from
    /// AiSensy or SES means accepted for delivery, and treating that as delivered
    /// is how notification systems end up lying to their operators.
    /// </summary>
    public void MarkAccepted(string? providerMessageId, string? providerReference, DateTime nowUtc)
    {
        // A delivery/read webhook can beat our own response handling, so never
        // walk a further-along row back to Accepted.
        if (Status is NotificationStatus.Delivered or NotificationStatus.Read) return;

        Status                    = NotificationStatus.Accepted;
        ProviderMessageId         = providerMessageId ?? ProviderMessageId;
        ProviderResponseReference = providerReference ?? ProviderResponseReference;
        AcceptedAt              ??= nowUtc;
        NextAttemptAt             = null;
        LockedBy                  = null;
        LockedAt                  = null;
        UpdatedAt                 = nowUtc;
    }

    /// <summary>Confirmed at the recipient. For in-app this is immediate; for email/WhatsApp it comes from a provider event.</summary>
    public void MarkDelivered(DateTime nowUtc)
    {
        if (Status == NotificationStatus.Read) return;   // Read is further along; do not regress.
        if (IsTerminal) return;

        Status        = NotificationStatus.Delivered;
        AcceptedAt  ??= nowUtc;
        DeliveredAt ??= nowUtc;
        NextAttemptAt = null;
        UpdatedAt     = nowUtc;
    }

    public void MarkRead(DateTime nowUtc)
    {
        if (IsTerminal) return;

        Status        = NotificationStatus.Read;
        AcceptedAt  ??= nowUtc;
        DeliveredAt ??= nowUtc;
        ReadAt      ??= nowUtc;
        NextAttemptAt = null;
        UpdatedAt     = nowUtc;
    }

    /// <summary>
    /// Transient failure: keep the row alive and come back later at
    /// <paramref name="nextAttemptAt"/>. Status stays Pending so the queue picks
    /// it up again -- Processing would look like a worker still holds it.
    /// </summary>
    public void ScheduleRetry(string reason, DateTime nextAttemptAt, DateTime nowUtc)
    {
        if (IsTerminal) return;

        Status        = NotificationStatus.Pending;
        FailureReason = Truncate(reason);
        NextAttemptAt = nextAttemptAt;
        LockedBy      = null;
        LockedAt      = null;
        UpdatedAt     = nowUtc;
    }

    /// <summary>
    /// Terminal failure -- a permanent provider error, or retries exhausted.
    /// Everything needed to explain it later is preserved; nothing is deleted.
    /// </summary>
    public void MarkFailed(string reason, DateTime nowUtc)
    {
        // A message that already reached the recipient must not be recorded as
        // failed because a later status callback was unhappy.
        if (Status is NotificationStatus.Delivered or NotificationStatus.Read) return;

        Status        = NotificationStatus.Failed;
        FailureReason = Truncate(reason);
        FailedAt    ??= nowUtc;
        NextAttemptAt = null;
        LockedBy      = null;
        LockedAt      = null;
        UpdatedAt     = nowUtc;
    }

    /// <summary>Abandoned before sending, e.g. the event was cancelled while the message sat in the queue.</summary>
    public void Cancel(string reason, DateTime nowUtc)
    {
        if (Status is NotificationStatus.Accepted or NotificationStatus.Delivered or NotificationStatus.Read) return;

        Status        = NotificationStatus.Cancelled;
        FailureReason = Truncate(reason);
        NextAttemptAt = null;
        LockedBy      = null;
        LockedAt      = null;
        UpdatedAt     = nowUtc;
    }

    /// <summary>Admin-triggered replay of a failed delivery: reopen it and let the worker pick it up.</summary>
    public void ResetForManualRetry(DateTime nowUtc)
    {
        Status        = NotificationStatus.Pending;
        NextAttemptAt = nowUtc;
        FailedAt      = null;
        LockedBy      = null;
        LockedAt      = null;
        UpdatedAt     = nowUtc;
    }

    /// <summary>Releases a row whose worker died mid-flight so it can be claimed again.</summary>
    public void ReleaseStaleLock(DateTime nowUtc)
    {
        if (Status != NotificationStatus.Processing) return;

        Status        = NotificationStatus.Pending;
        NextAttemptAt = nowUtc;
        LockedBy      = null;
        LockedAt      = null;
        UpdatedAt     = nowUtc;
    }

    private static string Truncate(string? reason)
        => string.IsNullOrWhiteSpace(reason) ? "Unknown error"
         : reason.Length <= 500 ? reason : reason[..500];
}
