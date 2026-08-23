using EventWOS.Domain.Common;
using EventWOS.Domain.Enums;

namespace EventWOS.Domain.Entities;

/// <summary>
/// One logical business notification for one recipient -- "crew member X was
/// assigned to event Y". It is channel-agnostic: the actual sending lives in
/// <see cref="NotificationDelivery"/> rows, one per channel, each with its own
/// state.
///
/// The text is NOT stored here. What is stored is <see cref="DataJson"/>: the
/// resolved placeholder values (crew name, event name, date, venue) captured at
/// creation time, from which each channel renders its own
/// <see cref="NotificationTemplate"/>. That matters because the same
/// notification reads differently per channel -- a WhatsApp line, an HTML
/// email, a short in-app row -- and because the values must reflect the world
/// as it was when the business event happened, not as it is when a retry
/// finally goes out an hour later.
///
/// There is no TenantId: EventWOS is single-tenant with role-based access, so
/// the ownership boundary is <see cref="RecipientUserId"/> plus the optional
/// <see cref="EventId"/>. Queries must always scope by recipient.
/// </summary>
public sealed class Notification : BaseEntity
{
    private Notification() { }

    public Notification(
        Guid recipientUserId,
        string templateCode,
        NotificationPriority priority,
        string dataJson,
        string idempotencyKey,
        Guid? eventId = null,
        Guid? actorUserId = null,
        string? correlationId = null)
    {
        if (recipientUserId == Guid.Empty)
            throw new ArgumentException("RecipientUserId is required.", nameof(recipientUserId));
        if (string.IsNullOrWhiteSpace(templateCode))
            throw new ArgumentException("TemplateCode is required.", nameof(templateCode));
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ArgumentException("IdempotencyKey is required.", nameof(idempotencyKey));

        RecipientUserId = recipientUserId;
        TemplateCode    = templateCode.Trim().ToUpperInvariant();
        Priority        = priority;
        DataJson        = string.IsNullOrWhiteSpace(dataJson) ? "{}" : dataJson;
        IdempotencyKey  = idempotencyKey.Trim();
        EventId         = eventId;
        ActorUserId     = actorUserId;
        CorrelationId   = correlationId;
        Status          = NotificationStatus.Pending;
    }

    /// <summary>The user this notification is for. Never a role or a group -- fan-out happens before this row is created.</summary>
    public Guid RecipientUserId { get; private set; }

    /// <summary>Optional event context, so notification history can be filtered per event.</summary>
    public Guid? EventId { get; private set; }

    /// <summary>Who caused this (the manager who assigned, the vendor who approved). Null for system-generated.</summary>
    public Guid? ActorUserId { get; private set; }

    /// <summary>Stable code such as CREW_ASSIGNMENT, resolved against <see cref="NotificationTemplate"/> per channel.</summary>
    public string TemplateCode { get; private set; } = default!;

    public NotificationPriority Priority { get; private set; }

    /// <summary>
    /// Rollup of the child deliveries for cheap listing and dashboards. It is
    /// derived state -- <see cref="RecalculateStatus"/> owns it -- and the
    /// per-channel truth always stays on the delivery rows.
    /// </summary>
    public NotificationStatus Status { get; private set; }

    /// <summary>Placeholder values as a JSON object, e.g. {"CrewName":"Asha","EventName":"Sunburn"}.</summary>
    public string DataJson { get; private set; } = "{}";

    /// <summary>
    /// Business-level dedupe key (business event + recipient + template),
    /// enforced by a unique index. An API retry, a double-clicked Assign button
    /// or a replayed outbox message therefore cannot produce a second message
    /// to the same person -- application-level checking alone would still race.
    /// </summary>
    public string IdempotencyKey { get; private set; } = default!;

    /// <summary>Ties this notification to the originating request in structured logs.</summary>
    public string? CorrelationId { get; private set; }

    /// <summary>When the recipient read it in-app. Drives the unread badge.</summary>
    public DateTime? ReadAt { get; private set; }

    private readonly List<NotificationDelivery> _deliveries = new();
    public IReadOnlyCollection<NotificationDelivery> Deliveries => _deliveries.AsReadOnly();

    public NotificationDelivery AddDelivery(
        NotificationChannel channel, string? destination, string provider, int templateVersion)
    {
        var delivery = new NotificationDelivery(Id, channel, destination, provider, templateVersion, Priority);
        _deliveries.Add(delivery);
        return delivery;
    }

    public void MarkReadByRecipient(DateTime whenUtc)
    {
        ReadAt ??= whenUtc;
        UpdatedAt = whenUtc;
    }

    /// <summary>
    /// Rolls the child delivery states up into one headline status: the best
    /// outcome any channel achieved. Reaching the recipient on WhatsApp while
    /// email bounced is a delivered notification, not a failed one -- it only
    /// counts as Failed when every channel failed.
    /// </summary>
    public void RecalculateStatus()
    {
        if (_deliveries.Count == 0) return;

        if (_deliveries.Any(d => d.Status == NotificationStatus.Read))
            Status = NotificationStatus.Read;
        else if (_deliveries.Any(d => d.Status == NotificationStatus.Delivered))
            Status = NotificationStatus.Delivered;
        else if (_deliveries.Any(d => d.Status == NotificationStatus.Accepted))
            Status = NotificationStatus.Accepted;
        else if (_deliveries.All(d => d.Status == NotificationStatus.Failed))
            Status = NotificationStatus.Failed;
        else if (_deliveries.All(d => d.Status is NotificationStatus.Cancelled or NotificationStatus.Failed))
            Status = NotificationStatus.Cancelled;
        else if (_deliveries.Any(d => d.Status == NotificationStatus.Processing))
            Status = NotificationStatus.Processing;
        else
            Status = NotificationStatus.Pending;

        UpdatedAt = DateTime.UtcNow;
    }
}
