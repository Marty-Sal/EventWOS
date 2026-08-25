using EventOpsOracle.Domain.Enums;

namespace EventOpsOracle.Application.Notifications.Contracts;

/// <summary>
/// A request to notify one person about one thing. This is what business
/// handlers hand to <see cref="Abstractions.INotificationDispatcher"/>; it says
/// nothing about channels or wording, because the handler's job is to describe
/// the business event, not to decide whether it becomes an email.
/// </summary>
/// <param name="TemplateCode">One of <see cref="NotificationTemplateCodes"/>.</param>
/// <param name="RecipientUserId">The user to notify.</param>
/// <param name="BusinessEventKey">
/// Stable identifier of the business fact behind this notification, e.g.
/// "assignment:{assignmentId}:accepted". Required rather than optional on
/// purpose: it is what makes the send idempotent, so a retried API call or a
/// double-clicked button cannot message someone twice. Use ids, never
/// timestamps -- a key containing "now" is a key that never matches.
/// </param>
/// <param name="Data">Placeholder values, keyed by <see cref="NotificationTokens"/>.</param>
/// <param name="Priority">Overrides the catalogue default when a specific case is more urgent.</param>
/// <param name="Channels">
/// Overrides channel selection. Normally left null so the platform decides from
/// the active templates and what contact details the recipient actually has.
/// </param>
public sealed record NotificationRequest(
    string TemplateCode,
    Guid RecipientUserId,
    string BusinessEventKey,
    IReadOnlyDictionary<string, string?>? Data = null,
    Guid? EventId = null,
    Guid? ActorUserId = null,
    NotificationPriority? Priority = null,
    IReadOnlyCollection<NotificationChannel>? Channels = null);

/// <summary>
/// Who to notify when the recipients are "everyone in this role on this event"
/// rather than a known list -- the thousand-crew case.
/// </summary>
public enum NotificationAudience
{
    /// <summary>Every crew member with an active assignment on the event.</summary>
    EventCrew = 0,

    /// <summary>Every vendor invited to or assigned on the event.</summary>
    EventVendors = 1,

    /// <summary>Both of the above.</summary>
    EventCrewAndVendors = 2,

    /// <summary>All Admin and Manager users -- operational alerts.</summary>
    Administrators = 3
}

/// <summary>
/// A fan-out request: describe the audience, not the recipients.
///
/// This exists so a business transaction stays small. Assigning 1,000 crew
/// writes ONE outbox row here; the worker resolves the audience and creates the
/// notification rows in batches afterwards. Writing 1,000 notifications inside
/// the API request would hold a long transaction open, and doing it before the
/// commit would mean a rollback had already sent nothing but paid for it.
/// </summary>
public sealed record NotificationFanOutRequest(
    string TemplateCode,
    NotificationAudience Audience,
    Guid EventId,
    string BusinessEventKey,
    IReadOnlyDictionary<string, string?>? Data = null,
    Guid? ActorUserId = null,
    NotificationPriority? Priority = null,
    IReadOnlyCollection<NotificationChannel>? Channels = null,
    /// <summary>Recipients to leave out -- normally the person who triggered the action.</summary>
    IReadOnlyCollection<Guid>? ExcludeUserIds = null);
