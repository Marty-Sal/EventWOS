using System.Text.Json;
using EventOpsOracle.Application.Interfaces;
using EventOpsOracle.Application.Notifications.Abstractions;
using EventOpsOracle.Application.Notifications.Contracts;
using EventOpsOracle.Domain.Entities;
using EventOpsOracle.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EventOpsOracle.Application.Notifications.Services;

/// <summary>
/// Turns claimed outbox rows into notification and delivery rows: resolves
/// audiences, looks up recipients, picks channels, and enforces idempotency.
///
/// It renders nothing and sends nothing. That separation is deliberate -- this
/// stage only ever touches our own database, so it is fast and safe to retry,
/// while the stage that can hang on a third party is isolated behind its own
/// queue with its own backoff.
/// </summary>
public sealed class OutboxProcessor
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true
    };

    private readonly IAppDbContext _db;
    private readonly INotificationWorkQueue _queue;
    private readonly NotificationChannelResolver _channels;
    private readonly ILogger<OutboxProcessor> _logger;

    public OutboxProcessor(
        IAppDbContext db,
        INotificationWorkQueue queue,
        NotificationChannelResolver channels,
        ILogger<OutboxProcessor> logger)
    {
        _db       = db;
        _queue    = queue;
        _channels = channels;
        _logger   = logger;
    }

    /// <summary>Returns how many outbox rows were handled, so the worker can decide whether to poll again immediately.</summary>
    public async Task<int> ProcessBatchAsync(string workerId, int batchSize, CancellationToken ct = default)
    {
        var messages = await _queue.ClaimOutboxBatchAsync(workerId, batchSize, ct);
        if (messages.Count == 0) return 0;

        // Templates are a tiny table read on every batch; loading the active set
        // once per batch avoids a query per notification without needing a cache
        // to invalidate when an admin edits the wording.
        var templates = await LoadActiveTemplatesAsync(ct);
        var now = DateTime.UtcNow;

        foreach (var message in messages)
        {
            try
            {
                var created = message.MessageType switch
                {
                    OutboxMessageTypes.NotificationRequested => await ExpandRequestedAsync(message, templates, ct),
                    OutboxMessageTypes.NotificationFanOut    => await ExpandFanOutAsync(message, templates, ct),
                    _ => throw new NotSupportedException($"Unknown outbox message type '{message.MessageType}'.")
                };

                message.MarkProcessed(now);

                _logger.LogInformation(
                    "Outbox {OutboxId} ({MessageType}) expanded into {NotificationCount} notification(s)",
                    message.Id, message.MessageType, created);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                // Malformed or unknown payloads will never succeed, so retrying
                // would just occupy a worker forever. Fail loudly and keep the row.
                message.MarkFailed($"{ex.GetType().Name}: {ex.Message}", now);
                _logger.LogError(ex, "Outbox {OutboxId} is unprocessable and was failed permanently", message.Id);
            }
            catch (Exception ex)
            {
                if (NotificationBackoff.ShouldRetry(message.AttemptCount))
                {
                    var retryAt = NotificationBackoff.NextAttemptAt(message.AttemptCount, now);
                    message.ScheduleRetry($"{ex.GetType().Name}: {ex.Message}", retryAt, now);
                    _logger.LogWarning(ex,
                        "Outbox {OutboxId} failed on attempt {Attempt}, retrying at {RetryAt:O}",
                        message.Id, message.AttemptCount, retryAt);
                }
                else
                {
                    message.MarkFailed($"{ex.GetType().Name}: {ex.Message}", now);
                    // An outbox row that never processes is a person who was
                    // never told something. Loud on purpose.
                    _logger.LogError(ex,
                        "Outbox {OutboxId} failed permanently after {Attempts} attempts -- recipients were NOT notified",
                        message.Id, message.AttemptCount);
                }
            }
        }

        await _queue.SaveChangesAsync(ct);
        return messages.Count;
    }

    private async Task<int> ExpandRequestedAsync(
        OutboxMessage message,
        IReadOnlyDictionary<string, Dictionary<NotificationChannel, NotificationTemplate>> templates,
        CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<NotificationRequestedPayload>(message.PayloadJson, JsonOptions)
                      ?? throw new JsonException("Payload deserialised to null.");

        var recipientIds = payload.Recipients.Select(r => r.RecipientUserId).Distinct().ToList();
        var recipients   = await LoadRecipientsAsync(recipientIds, ct);

        var items = payload.Recipients
            .Select(r => (r.RecipientUserId, r.BusinessEventKey, Data: r.Data))
            .ToList();

        return await CreateNotificationsAsync(
            payload.TemplateCode, payload.Priority, items, recipients, templates,
            payload.EventId, payload.ActorUserId, payload.Channels, message.CorrelationId, ct);
    }

    private async Task<int> ExpandFanOutAsync(
        OutboxMessage message,
        IReadOnlyDictionary<string, Dictionary<NotificationChannel, NotificationTemplate>> templates,
        CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<NotificationFanOutPayload>(message.PayloadJson, JsonOptions)
                      ?? throw new JsonException("Payload deserialised to null.");

        var audience = await ResolveAudienceAsync(payload.Audience, payload.EventId, ct);

        if (payload.ExcludeUserIds is { Count: > 0 })
        {
            var excluded = payload.ExcludeUserIds.ToHashSet();
            audience = audience.Where(id => !excluded.Contains(id)).ToList();
        }

        if (audience.Count == 0)
        {
            _logger.LogInformation(
                "Fan-out {OutboxId} for {TemplateCode} resolved to no recipients on event {EventId}",
                message.Id, payload.TemplateCode, payload.EventId);
            return 0;
        }

        var recipients = await LoadRecipientsAsync(audience, ct);

        // The business event key is shared by the whole audience, so each
        // recipient's idempotency key is scoped by their own user id -- one
        // person cannot be notified twice, and a replayed fan-out is a no-op.
        var items = audience
            .Select(id => (RecipientUserId: id, BusinessEventKey: payload.BusinessEventKey, payload.Data))
            .ToList();

        return await CreateNotificationsAsync(
            payload.TemplateCode, payload.Priority, items, recipients, templates,
            payload.EventId, payload.ActorUserId, payload.Channels, message.CorrelationId, ct);
    }

    private async Task<int> CreateNotificationsAsync(
        string templateCode,
        NotificationPriority priority,
        List<(Guid RecipientUserId, string BusinessEventKey, Dictionary<string, string?>? Data)> items,
        IReadOnlyDictionary<Guid, NotificationRecipient> recipients,
        IReadOnlyDictionary<string, Dictionary<NotificationChannel, NotificationTemplate>> templates,
        Guid? eventId,
        Guid? actorUserId,
        List<NotificationChannel>? channelOverride,
        string? correlationId,
        CancellationToken ct)
    {
        if (!templates.TryGetValue(templateCode, out var templatesForCode) || templatesForCode.Count == 0)
        {
            // A missing template is a configuration problem, not a transient
            // one, and a notification nobody can render is worth a loud log
            // rather than a retry loop.
            _logger.LogError(
                "No active template for {TemplateCode} -- {RecipientCount} recipient(s) will not be notified",
                templateCode, items.Count);
            return 0;
        }

        var keys = items
            .Select(i => NotificationKeys.Build(i.BusinessEventKey, templateCode, i.RecipientUserId))
            .ToList();

        // Bulk pre-check so a replayed outbox row does not hammer the unique
        // index once per recipient. The index is still the real guarantee; this
        // just keeps the common case cheap.
        var existing = await _db.Notifications
            .Where(n => keys.Contains(n.IdempotencyKey))
            .Select(n => n.IdempotencyKey)
            .ToListAsync(ct);
        var alreadySent = existing.ToHashSet();

        var created = 0;

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var key  = keys[i];

            if (alreadySent.Contains(key)) continue;
            if (!recipients.TryGetValue(item.RecipientUserId, out var recipient)) continue;

            var data = item.Data is null
                ? new Dictionary<string, string?>()
                : new Dictionary<string, string?>(item.Data);

            // Always available to templates, so a wording change can add a
            // greeting without every call site being updated.
            data.TryAdd(NotificationTokens.RecipientName, recipient.FullName);

            var resolved = _channels.Resolve(templateCode, recipient, templatesForCode, channelOverride);
            if (resolved.Count == 0)
            {
                _logger.LogWarning(
                    "No usable channel for {TemplateCode} to user {UserId} (email={HasEmail}, mobile={HasMobile})",
                    templateCode, recipient.UserId,
                    !string.IsNullOrWhiteSpace(recipient.Email), !string.IsNullOrWhiteSpace(recipient.Mobile));
                continue;
            }

            var notification = new Notification(
                recipient.UserId, templateCode, priority,
                JsonSerializer.Serialize(data), key, eventId, actorUserId, correlationId);

            foreach (var channel in resolved)
                notification.AddDelivery(channel.Channel, channel.Destination, channel.ProviderName, channel.Template.Version);

            _db.Notifications.Add(notification);
            alreadySent.Add(key);
            created++;
        }

        return created;
    }

    private async Task<List<Guid>> ResolveAudienceAsync(
        NotificationAudience audience, Guid eventId, CancellationToken ct)
    {
        // Only assignments that still mean something: someone whose invite was
        // revoked or who was rejected should not get event chatter. Same idea for the
        // shift: a row whose shift has been archived is not work any more, so its
        // holder is not part of this event's audience -- the rule spelled out in
        // VendorEventParticipationRules.LiveShiftRuleReference, applied here so the
        // read paths and the notification audience agree on who is "on" an event.
        var liveStatuses = new[]
        {
            AssignmentStatus.Invited, AssignmentStatus.Confirmed, AssignmentStatus.VendorApproved,
            AssignmentStatus.PendingManagerApproval, AssignmentStatus.ManagerApproved, AssignmentStatus.Attended
        };

        switch (audience)
        {
            case NotificationAudience.EventCrew:
                return await _db.EventAssignments
                    .Where(a => a.EventId == eventId && a.CrewId != null && liveStatuses.Contains(a.Status))
                    .Where(a => a.ShiftId == null || _db.EventShifts.Any(s => s.Id == a.ShiftId))
                    .Select(a => a.CrewId!.Value).Distinct().ToListAsync(ct);

            case NotificationAudience.EventVendors:
                return await _db.EventAssignments
                    .Where(a => a.EventId == eventId && a.VendorId != null && liveStatuses.Contains(a.Status))
                    .Where(a => a.ShiftId == null || _db.EventShifts.Any(s => s.Id == a.ShiftId))
                    .Select(a => a.VendorId!.Value).Distinct().ToListAsync(ct);

            case NotificationAudience.EventCrewAndVendors:
                var crew    = await ResolveAudienceAsync(NotificationAudience.EventCrew, eventId, ct);
                var vendors = await ResolveAudienceAsync(NotificationAudience.EventVendors, eventId, ct);
                return crew.Union(vendors).ToList();

            case NotificationAudience.Administrators:
                return await _db.Users
                    .Where(u => (u.Role == UserRole.Admin || u.Role == UserRole.Manager) && u.Status == UserStatus.Active)
                    .Select(u => u.Id).ToListAsync(ct);

            default:
                throw new NotSupportedException($"Unknown audience '{audience}'.");
        }
    }

    private async Task<IReadOnlyDictionary<Guid, NotificationRecipient>> LoadRecipientsAsync(
        List<Guid> userIds, CancellationToken ct)
        => await _db.Users
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new NotificationRecipient(u.Id, u.FullName, u.Email, u.Mobile))
            .ToDictionaryAsync(r => r.UserId, ct);

    private async Task<IReadOnlyDictionary<string, Dictionary<NotificationChannel, NotificationTemplate>>>
        LoadActiveTemplatesAsync(CancellationToken ct)
    {
        var rows = await _db.NotificationTemplates
            .Where(t => t.IsActive && t.Language == "en")
            .ToListAsync(ct);

        return rows
            .GroupBy(t => t.Code, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(t => t.Channel).ToDictionary(c => c.Key, c => c.Last()),
                StringComparer.OrdinalIgnoreCase);
    }
}
