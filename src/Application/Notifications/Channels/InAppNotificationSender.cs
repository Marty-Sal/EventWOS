using EventWOS.Application.Interfaces;
using EventWOS.Application.Notifications.Abstractions;
using EventWOS.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace EventWOS.Application.Notifications.Channels;

/// <summary>
/// In-app channel. The notification row itself IS the delivery -- it is already
/// persisted and the recipient will see it in their list whenever they next open
/// the app -- so this sender only pushes the live SignalR nudge to anyone
/// currently connected.
///
/// That is why a push failure is not a delivery failure: the message is durably
/// stored either way. Before this platform existed, in-app notifications were
/// SignalR pushes and nothing else, so a user who was offline at that moment
/// simply never learned what happened.
/// </summary>
public sealed class InAppNotificationSender : INotificationChannelSender
{
    private readonly INotificationPusher _pusher;
    private readonly ILogger<InAppNotificationSender> _logger;

    public InAppNotificationSender(INotificationPusher pusher, ILogger<InAppNotificationSender> logger)
    {
        _pusher = pusher;
        _logger = logger;
    }

    public NotificationChannel Channel => NotificationChannel.InApp;

    public string ProviderName => "SignalR";

    /// <summary>Always available: no third party, no credentials.</summary>
    public bool IsConfigured => true;

    public async Task<ChannelSendResult> SendAsync(NotificationSendContext context, CancellationToken ct = default)
    {
        try
        {
            await _pusher.PushToUserAsync(
                context.RecipientUserId,
                "NotificationReceived",
                new
                {
                    id       = context.Notification.Id,
                    code     = context.Notification.TemplateCode,
                    title    = context.Message.Subject ?? context.Notification.TemplateCode,
                    body     = context.Message.Body,
                    eventId  = context.Notification.EventId,
                    priority = context.Notification.Priority.ToString(),
                    sentAt   = DateTime.UtcNow
                },
                ct);

            return ChannelSendResult.Accepted(detail: "Pushed to connected clients");
        }
        catch (Exception ex)
        {
            // Not a retry: the row is stored, the badge will be correct on next
            // load, and hammering SignalR would not change that.
            _logger.LogWarning(ex,
                "SignalR push failed for notification {NotificationId}; the in-app row is stored regardless",
                context.Notification.Id);

            return ChannelSendResult.Accepted(detail: "Stored; live push unavailable");
        }
    }
}
