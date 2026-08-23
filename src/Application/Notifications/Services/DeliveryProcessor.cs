using System.Text.Json;
using EventWOS.Application.Interfaces;
using EventWOS.Application.Notifications.Abstractions;
using EventWOS.Application.Notifications.Rendering;
using EventWOS.Domain.Entities;
using EventWOS.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EventWOS.Application.Notifications.Services;

/// <summary>
/// The stage that actually sends: claims due deliveries, renders each one for its
/// channel, hands it to that channel's provider, and records the outcome.
///
/// All retry policy lives here rather than in the senders, so every provider is
/// retried the same way and a new provider cannot quietly invent its own rules.
/// Senders report Accepted / Transient / Permanent / Skipped; this class decides
/// what that means for the row.
/// </summary>
public sealed class DeliveryProcessor
{
    private readonly IAppDbContext _db;
    private readonly INotificationWorkQueue _queue;
    private readonly INotificationTemplateRenderer _renderer;
    private readonly IReadOnlyDictionary<NotificationChannel, INotificationChannelSender> _senders;
    private readonly ILogger<DeliveryProcessor> _logger;

    public DeliveryProcessor(
        IAppDbContext db,
        INotificationWorkQueue queue,
        INotificationTemplateRenderer renderer,
        IEnumerable<INotificationChannelSender> senders,
        ILogger<DeliveryProcessor> logger)
    {
        _db       = db;
        _queue    = queue;
        _renderer = renderer;
        _logger   = logger;
        _senders  = senders.GroupBy(s => s.Channel).ToDictionary(g => g.Key, g => g.Last());
    }

    public async Task<int> ProcessBatchAsync(string workerId, int batchSize, CancellationToken ct = default)
    {
        var deliveries = await _queue.ClaimDeliveryBatchAsync(workerId, batchSize, ct);
        if (deliveries.Count == 0) return 0;

        var notificationIds = deliveries.Select(d => d.NotificationId).Distinct().ToList();

        // Include the sibling deliveries: the parent's rollup status is derived
        // from all of them, so recalculating without them would report a
        // notification as failed while another channel was still pending.
        var notifications = await _db.Notifications
            .Include(n => n.Deliveries)
            .Where(n => notificationIds.Contains(n.Id))
            .ToDictionaryAsync(n => n.Id, ct);

        var templates = await _db.NotificationTemplates
            .Where(t => t.IsActive && t.Language == "en")
            .ToListAsync(ct);

        var templateLookup = templates
            .GroupBy(t => (t.Code.ToUpperInvariant(), t.Channel))
            .ToDictionary(g => g.Key, g => g.Last());

        foreach (var delivery in deliveries)
        {
            var now = DateTime.UtcNow;

            if (!notifications.TryGetValue(delivery.NotificationId, out var notification))
            {
                // Orphan: the parent was hard-deleted. Nothing to send and
                // nothing to fix, so close the row instead of retrying forever.
                delivery.Cancel("Parent notification no longer exists", now);
                continue;
            }

            if (!templateLookup.TryGetValue((notification.TemplateCode.ToUpperInvariant(), delivery.Channel), out var template))
            {
                // The template was deactivated after the delivery was created --
                // an admin turning a channel off mid-flight, not a failure.
                delivery.Cancel($"No active {delivery.Channel} template for {notification.TemplateCode}", now);
                _logger.LogInformation(
                    "Delivery {DeliveryId} cancelled: {Channel} template for {TemplateCode} is no longer active",
                    delivery.Id, delivery.Channel, notification.TemplateCode);
                continue;
            }

            if (!_senders.TryGetValue(delivery.Channel, out var sender) || !sender.IsConfigured)
            {
                // Provider not configured (a key was removed, say). Transient on
                // purpose: it usually means someone is mid-way through setup, and
                // the message should survive that.
                RecordTransient(delivery, $"No configured sender for {delivery.Channel}", now);
                continue;
            }

            try
            {
                var data     = ParseData(notification.DataJson);
                var rendered = _renderer.Render(template, data);

                if (rendered.MissingTokens.Count > 0)
                {
                    // Still sent -- a partly filled message beats silence -- but
                    // surfaced, because it means a call site or a template is wrong.
                    _logger.LogWarning(
                        "Template {TemplateCode}/{Channel} is missing values for {MissingTokens} on notification {NotificationId}",
                        notification.TemplateCode, delivery.Channel,
                        string.Join(", ", rendered.MissingTokens), notification.Id);
                }

                var context = new NotificationSendContext(notification, delivery, template, rendered);
                var result  = await sender.SendAsync(context, ct);

                ApplyResult(delivery, result, now);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutdown, not failure. Release the row so the next worker (or
                // the next boot) picks it up immediately rather than waiting for
                // the stale-lock sweep.
                delivery.ScheduleRetry("Worker shutting down", now, now);
                throw;
            }
            catch (Exception ex)
            {
                // A sender that throws is a bug in that sender; treat it as
                // transient so one bad provider cannot destroy the message.
                RecordTransient(delivery, $"{ex.GetType().Name}: {ex.Message}", now);
                _logger.LogError(ex,
                    "Sender {Channel} threw for delivery {DeliveryId}", delivery.Channel, delivery.Id);
            }
        }

        foreach (var notification in notifications.Values)
            notification.RecalculateStatus();

        await _queue.SaveChangesAsync(ct);
        return deliveries.Count;
    }

    private void ApplyResult(NotificationDelivery delivery, ChannelSendResult result, DateTime now)
    {
        switch (result.Outcome)
        {
            case ChannelSendOutcome.Accepted:
                delivery.MarkAccepted(result.ProviderMessageId, result.ProviderReference, now);

                // In-app has no provider to confirm anything: the row IS the
                // delivery, so waiting for a webhook that will never arrive
                // would leave every in-app notification stuck at Accepted.
                if (delivery.Channel == NotificationChannel.InApp)
                    delivery.MarkDelivered(now);

                _logger.LogInformation(
                    "Delivery {DeliveryId} accepted by {Provider} ({Channel}) providerMessageId={ProviderMessageId}",
                    delivery.Id, delivery.Provider, delivery.Channel, result.ProviderMessageId ?? "(none)");
                break;

            case ChannelSendOutcome.PermanentFailure:
                delivery.MarkFailed(result.Detail ?? "Permanent provider failure", now);
                _logger.LogWarning(
                    "Delivery {DeliveryId} failed permanently on {Channel}: {Detail}",
                    delivery.Id, delivery.Channel, result.Detail);
                break;

            case ChannelSendOutcome.Skipped:
                delivery.Cancel(result.Detail ?? "Nothing to send", now);
                break;

            default:
                RecordTransient(delivery, result.Detail ?? "Transient provider failure", now);
                break;
        }
    }

    private void RecordTransient(NotificationDelivery delivery, string detail, DateTime now)
    {
        if (NotificationBackoff.ShouldRetry(delivery.AttemptCount))
        {
            var retryAt = NotificationBackoff.NextAttemptAt(delivery.AttemptCount, now);
            delivery.ScheduleRetry(detail, retryAt, now);
            _logger.LogInformation(
                "Delivery {DeliveryId} retrying at {RetryAt:O} (attempt {Attempt}/{Max}): {Detail}",
                delivery.Id, retryAt, delivery.AttemptCount, NotificationBackoff.MaxAttempts, detail);
        }
        else
        {
            delivery.MarkFailed($"Gave up after {delivery.AttemptCount} attempts: {detail}", now);
            _logger.LogError(
                "Delivery {DeliveryId} exhausted {Attempts} attempts on {Channel} to {Destination}: {Detail}",
                delivery.Id, delivery.AttemptCount, delivery.Channel, delivery.Destination, detail);
        }
    }

    private static IReadOnlyDictionary<string, string?> ParseData(string dataJson)
    {
        if (string.IsNullOrWhiteSpace(dataJson)) return new Dictionary<string, string?>();

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string?>>(dataJson)
                   ?? new Dictionary<string, string?>();
        }
        catch (JsonException)
        {
            // Corrupt payload should not stop the send: the template still has
            // static text, and missing tokens are already reported.
            return new Dictionary<string, string?>();
        }
    }
}
