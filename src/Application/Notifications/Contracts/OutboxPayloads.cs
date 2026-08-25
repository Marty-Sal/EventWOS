using EventOpsOracle.Domain.Enums;

namespace EventOpsOracle.Application.Notifications.Contracts;

/// <summary>
/// Message-type discriminators written to outbox_messages.message_type. The
/// worker switches on these, so they are part of the persisted contract: never
/// rename one without a migration for rows already in flight.
/// </summary>
public static class OutboxMessageTypes
{
    /// <summary>Payload is <see cref="NotificationRequestedPayload"/> -- a known list of recipients.</summary>
    public const string NotificationRequested = "NotificationRequested";

    /// <summary>Payload is <see cref="NotificationFanOutPayload"/> -- an audience the worker resolves.</summary>
    public const string NotificationFanOut = "NotificationFanOut";
}

/// <summary>One recipient's notification, as persisted in the outbox payload.</summary>
public sealed record NotificationRecipientPayload(
    Guid RecipientUserId,
    string BusinessEventKey,
    Dictionary<string, string?>? Data);

/// <summary>
/// A batch of explicit recipients for one template. Batched rather than one row
/// per recipient so that assigning 40 crew does not write 40 outbox rows, while
/// still keeping each payload small enough to read and requeue by hand.
/// </summary>
public sealed record NotificationRequestedPayload(
    string TemplateCode,
    NotificationPriority Priority,
    List<NotificationRecipientPayload> Recipients,
    Guid? EventId,
    Guid? ActorUserId,
    List<NotificationChannel>? Channels);

/// <summary>
/// An audience to expand at processing time. Keeps the business transaction to a
/// single row no matter how many people the event involves.
/// </summary>
public sealed record NotificationFanOutPayload(
    string TemplateCode,
    NotificationPriority Priority,
    NotificationAudience Audience,
    Guid EventId,
    string BusinessEventKey,
    Dictionary<string, string?>? Data,
    Guid? ActorUserId,
    List<NotificationChannel>? Channels,
    List<Guid>? ExcludeUserIds);
