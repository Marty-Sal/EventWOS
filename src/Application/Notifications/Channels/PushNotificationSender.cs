using EventWOS.Application.Notifications.Abstractions;
using EventWOS.Application.Notifications.Contracts;
using EventWOS.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace EventWOS.Application.Notifications.Channels;

/// <summary>
/// The Push channel. One delivery row in, up to N device pushes out.
///
/// Push is the only channel whose destination is a set. Email has one address and
/// WhatsApp one number, so their delivery rows carry the destination inline; a
/// user's push endpoints change without them ever logging in -- a browser can
/// drop a subscription overnight and mint a new one. So the delivery row here
/// addresses the USER and the fan-out happens at send time, against whatever is
/// live at that moment.
///
/// The aggregation rules matter as much as the sending:
///
///   * At least one endpoint accepted -> Accepted. The user got told. A dead
///     laptop subscription must not fail a notification their phone received.
///   * No live endpoints at all -> Skipped, not Failed. The overwhelming majority
///     of users never enable push, and that is not an incident.
///   * Every endpoint dead or permanently broken -> PermanentFailure. Retrying
///     reaches the same nobody five more times.
///   * Anything transient still outstanding -> TransientFailure, so the whole
///     delivery is retried on the normal backoff.
///
/// 404/410 is the only outcome that retires a registration, because it is the
/// only one that means the endpoint itself is gone rather than the attempt having
/// gone wrong.
/// </summary>
public sealed class PushNotificationSender : INotificationChannelSender
{
    private readonly IReadOnlyDictionary<PushProvider, IPushNotificationProvider> _providers;
    private readonly IPushRegistrationStore _store;
    private readonly ILogger<PushNotificationSender> _logger;

    public PushNotificationSender(
        IEnumerable<IPushNotificationProvider> providers,
        IPushRegistrationStore store,
        ILogger<PushNotificationSender> logger)
    {
        _providers = providers
            .GroupBy(p => p.Provider)
            .ToDictionary(g => g.Key, g => g.Last());
        _store  = store;
        _logger = logger;
    }

    public NotificationChannel Channel => NotificationChannel.Push;

    /// <summary>
    /// Names the transport actually configured, because that is what a support
    /// question ("which provider took this?") needs to be answerable.
    /// </summary>
    public string ProviderName
        => _providers.Values.FirstOrDefault(p => p.IsConfigured)?.ProviderName ?? "WebPush";

    public bool IsConfigured => _providers.Values.Any(p => p.IsConfigured);

    public async Task<ChannelSendResult> SendAsync(NotificationSendContext context, CancellationToken ct = default)
    {
        var endpoints = await _store.GetActiveEndpointsAsync(context.RecipientUserId, ct);
        if (endpoints.Count == 0)
            return ChannelSendResult.Skip("Recipient has no active push registrations");

        // The badge is the server's number, read now rather than counted from
        // sends. If the user has already read this on another device the count
        // reflects that, which is the entire point of the server being
        // authoritative about unread state.
        var badge = await _store.GetUnreadCountAsync(context.RecipientUserId, ct);

        var message = BuildMessage(context, badge);

        var outcomes  = new List<PushEndpointOutcome>(endpoints.Count);
        var accepted  = 0;
        var transient = 0;
        var gone      = 0;
        var permanent = 0;
        string? firstMessageId = null;
        string? firstProblem   = null;

        foreach (var endpoint in endpoints)
        {
            if (!_providers.TryGetValue(endpoint.Provider, out var provider) || !provider.IsConfigured)
            {
                // A registration for a transport that is not switched on -- an FCM
                // row while only VAPID is configured, say. Transient, because the
                // fix is configuration, and the row is still perfectly good.
                transient++;
                firstProblem ??= $"No configured provider for {endpoint.Provider}";
                outcomes.Add(new PushEndpointOutcome(endpoint.RegistrationId, PushSendOutcome.TransientFailure));
                continue;
            }

            PushSendResult result;
            try
            {
                result = await provider.SendAsync(message, endpoint, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutdown mid-batch. Leave the delivery to be retried rather
                // than recording a failure that never really happened.
                throw;
            }
            catch (Exception ex)
            {
                // A provider that throws is a bug in the provider, not a verdict
                // on the subscription -- treat it as transient and keep going, so
                // one broken endpoint cannot stop the other devices.
                _logger.LogError(ex,
                    "Push provider {Provider} threw for registration {RegistrationId} on notification {NotificationId}",
                    endpoint.Provider, endpoint.RegistrationId, context.Notification.Id);
                result = PushSendResult.Transient("Provider threw");
            }

            outcomes.Add(new PushEndpointOutcome(endpoint.RegistrationId, result.Outcome, result.Detail));

            switch (result.Outcome)
            {
                case PushSendOutcome.Accepted:
                    accepted++;
                    firstMessageId ??= result.ProviderMessageId;
                    break;
                case PushSendOutcome.TransientFailure:
                    transient++;
                    firstProblem ??= result.Detail;
                    break;
                case PushSendOutcome.EndpointGone:
                    gone++;
                    firstProblem ??= result.Detail;
                    break;
                default:
                    permanent++;
                    firstProblem ??= result.Detail;
                    break;
            }
        }

        // Bookkeeping first: a retired subscription must be retired even if the
        // delivery as a whole is about to be retried, or the next attempt walks
        // into the same dead endpoint.
        await _store.ApplyOutcomesAsync(outcomes, ct);

        _logger.LogInformation(
            "Push fan-out for notification {NotificationId} delivery {DeliveryId}: {Accepted} accepted, " +
            "{Transient} transient, {Gone} gone, {Permanent} permanent across {Total} registrations",
            context.Notification.Id, context.Delivery.Id, accepted, transient, gone, permanent, endpoints.Count);

        if (accepted > 0)
            return ChannelSendResult.Accepted(
                firstMessageId,
                detail: endpoints.Count > 1 ? $"Accepted by {accepted} of {endpoints.Count} devices" : null);

        if (transient > 0)
            return ChannelSendResult.Transient(firstProblem ?? "Push service unavailable");

        // Everything either gone or permanently rejected.
        return ChannelSendResult.Permanent(
            gone == endpoints.Count
                ? "All push registrations are no longer valid"
                : firstProblem ?? "Push rejected for every registration");
    }

    private static PushMessage BuildMessage(NotificationSendContext context, int badgeCount)
    {
        var rendered = context.Message;

        // Subject is the headline where a template has one; otherwise the body
        // doubles as the title, since a notification with no title reads as
        // broken on every platform.
        var title = string.IsNullOrWhiteSpace(rendered.Subject) ? "EventWOS" : rendered.Subject!.Trim();

        return new PushMessage(
            Title:            title,
            Body:             rendered.Body,
            NotificationId:   context.Notification.Id,
            NotificationType: context.Notification.TemplateCode,
            DeepLink:         PushDeepLinks.For(context.Notification.TemplateCode, context.Data),
            BadgeCount:       badgeCount,
            Priority:         context.Notification.Priority,
            Sound:            "default",
            Data:             context.Data);
    }
}
