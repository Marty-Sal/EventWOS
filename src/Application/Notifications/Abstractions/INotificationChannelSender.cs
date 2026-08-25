using EventOpsOracle.Application.Notifications.Rendering;
using EventOpsOracle.Domain.Entities;
using EventOpsOracle.Domain.Enums;

namespace EventOpsOracle.Application.Notifications.Abstractions;

/// <summary>
/// One channel's transport. Implementations do exactly one thing: hand a
/// rendered message to a provider and report what happened. They do not decide
/// whether to retry, do not touch the database, and do not throw for ordinary
/// provider failures -- the delivery processor owns all of that, so retry policy
/// lives in one place instead of being reinvented per provider.
/// </summary>
public interface INotificationChannelSender
{
    NotificationChannel Channel { get; }

    /// <summary>Provider name recorded on the delivery row, e.g. "AiSensy", "AmazonSes", "SignalR".</summary>
    string ProviderName { get; }

    /// <summary>
    /// True when this sender is actually usable -- credentials present, config
    /// valid. A sender that reports false is skipped during channel selection
    /// rather than queueing messages that can only fail.
    /// </summary>
    bool IsConfigured { get; }

    Task<ChannelSendResult> SendAsync(NotificationSendContext context, CancellationToken ct = default);
}

/// <summary>
/// Everything a sender needs for one attempt. Passed as one object rather than
/// four parameters because senders differ in what they use: the in-app sender
/// needs the recipient id, AiSensy needs the provider template name and the
/// positional parameters, SES needs the subject and the HTML body.
/// </summary>
public sealed record NotificationSendContext(
    Notification Notification,
    NotificationDelivery Delivery,
    NotificationTemplate Template,
    RenderedNotification Message,
    IReadOnlyDictionary<string, string?> Data)
{
    public Guid RecipientUserId => Notification.RecipientUserId;
    public string? Destination  => Delivery.Destination;
}

/// <summary>How a send attempt ended. Deliberately three outcomes, not a bool.</summary>
public enum ChannelSendOutcome
{
    /// <summary>The provider took the message. Not the same as delivered.</summary>
    Accepted = 0,

    /// <summary>
    /// Worth trying again: timeout, 429, 5xx, network blip. The processor
    /// schedules a backoff retry.
    /// </summary>
    TransientFailure = 1,

    /// <summary>
    /// Retrying cannot help: invalid recipient, unapproved template, message
    /// rejected, dead device token. Fails immediately instead of burning five
    /// attempts to reach the same conclusion.
    /// </summary>
    PermanentFailure = 2,

    /// <summary>
    /// Nothing to send and nothing wrong -- e.g. the recipient has no mobile
    /// number for a WhatsApp delivery. Recorded as cancelled, not failed, so it
    /// does not pollute the failure metrics operators watch.
    /// </summary>
    Skipped = 3
}

/// <param name="ProviderMessageId">Provider's id for the message, when it gives one. The webhook correlation key.</param>
/// <param name="ProviderReference">Secondary reference (request id, submission id) for support escalation.</param>
/// <param name="Detail">Short human-readable explanation. Must never contain credentials or full payloads.</param>
public sealed record ChannelSendResult(
    ChannelSendOutcome Outcome,
    string? ProviderMessageId = null,
    string? ProviderReference = null,
    string? Detail = null)
{
    public static ChannelSendResult Accepted(string? messageId = null, string? reference = null, string? detail = null)
        => new(ChannelSendOutcome.Accepted, messageId, reference, detail);

    public static ChannelSendResult Transient(string detail)
        => new(ChannelSendOutcome.TransientFailure, Detail: detail);

    public static ChannelSendResult Permanent(string detail)
        => new(ChannelSendOutcome.PermanentFailure, Detail: detail);

    public static ChannelSendResult Skip(string detail)
        => new(ChannelSendOutcome.Skipped, Detail: detail);
}
