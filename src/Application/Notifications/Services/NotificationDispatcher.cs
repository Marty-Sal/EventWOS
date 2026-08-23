using System.Text.Json;
using EventWOS.Application.Interfaces;
using EventWOS.Application.Notifications.Abstractions;
using EventWOS.Application.Notifications.Contracts;
using EventWOS.Domain.Entities;
using EventWOS.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace EventWOS.Application.Notifications.Services;

/// <summary>
/// Stages notification work as transactional-outbox rows on the caller's
/// DbContext. It performs no I/O of its own and calls no provider -- the
/// expensive, failure-prone part happens in the background worker, after the
/// business transaction has safely committed.
/// </summary>
public sealed class NotificationDispatcher : INotificationDispatcher
{
    /// <summary>
    /// Recipients per outbox row. Small enough that one row stays easy to read
    /// and requeue when something goes wrong, large enough that a normal
    /// multi-crew assignment is still a single row.
    /// </summary>
    private const int RecipientsPerMessage = 100;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // Enums as strings: an outbox payload is operational data an engineer
        // reads at 2am, and "Priority": 1 tells them nothing.
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IAppDbContext _db;
    private readonly ILogger<NotificationDispatcher> _logger;

    public NotificationDispatcher(IAppDbContext db, ILogger<NotificationDispatcher> logger)
    {
        _db     = db;
        _logger = logger;
    }

    public void Enqueue(NotificationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Enqueue(new[] { request });
    }

    public void Enqueue(IEnumerable<NotificationRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);

        // Group by everything that defines "the same message", so one row can
        // carry many recipients. Channel overrides are part of the grouping key
        // because they change what gets sent.
        var groups = requests
            .Where(r => r is not null && r.RecipientUserId != Guid.Empty && !string.IsNullOrWhiteSpace(r.TemplateCode))
            .GroupBy(r => (
                r.TemplateCode,
                Priority: r.Priority ?? NotificationPolicy.DefaultPriority(r.TemplateCode),
                r.EventId,
                r.ActorUserId,
                ChannelKey: ChannelKey(r.Channels)));

        foreach (var group in groups)
        {
            var channels = group.First().Channels?.ToList();

            foreach (var chunk in group.Chunk(RecipientsPerMessage))
            {
                var recipients = chunk
                    // Same person twice in one call is a caller bug, not an
                    // instruction to message them twice. The unique index would
                    // catch it later; collapsing here saves the wasted work.
                    .GroupBy(r => (r.RecipientUserId, r.BusinessEventKey))
                    .Select(g => g.First())
                    .Select(r => new NotificationRecipientPayload(
                        r.RecipientUserId,
                        RequireBusinessKey(r),
                        r.Data?.ToDictionary(kv => kv.Key, kv => kv.Value)))
                    .ToList();

                if (recipients.Count == 0) continue;

                var payload = new NotificationRequestedPayload(
                    group.Key.TemplateCode,
                    group.Key.Priority,
                    recipients,
                    group.Key.EventId,
                    group.Key.ActorUserId,
                    channels?.ToList());

                _db.OutboxMessages.Add(new OutboxMessage(
                    aggregateType: group.Key.EventId.HasValue ? "Event" : "User",
                    aggregateId:   group.Key.EventId,
                    messageType:   OutboxMessageTypes.NotificationRequested,
                    payloadJson:   JsonSerializer.Serialize(payload, JsonOptions)));

                _logger.LogInformation(
                    "Notification queued: {TemplateCode} for {RecipientCount} recipient(s), priority {Priority}, event {EventId}",
                    group.Key.TemplateCode, recipients.Count, group.Key.Priority, group.Key.EventId);
            }
        }
    }

    public void EnqueueFanOut(NotificationFanOutRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.TemplateCode))
            throw new ArgumentException("TemplateCode is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.BusinessEventKey))
            throw new ArgumentException("BusinessEventKey is required -- it is what makes the send idempotent.", nameof(request));

        var payload = new NotificationFanOutPayload(
            request.TemplateCode,
            request.Priority ?? NotificationPolicy.DefaultPriority(request.TemplateCode),
            request.Audience,
            request.EventId,
            request.BusinessEventKey,
            request.Data?.ToDictionary(kv => kv.Key, kv => kv.Value),
            request.ActorUserId,
            request.Channels?.ToList(),
            request.ExcludeUserIds?.ToList());

        _db.OutboxMessages.Add(new OutboxMessage(
            aggregateType: "Event",
            aggregateId:   request.EventId,
            messageType:   OutboxMessageTypes.NotificationFanOut,
            payloadJson:   JsonSerializer.Serialize(payload, JsonOptions)));

        _logger.LogInformation(
            "Notification fan-out queued: {TemplateCode} to {Audience} of event {EventId}",
            request.TemplateCode, request.Audience, request.EventId);
    }

    private static string RequireBusinessKey(NotificationRequest request)
        => string.IsNullOrWhiteSpace(request.BusinessEventKey)
            ? throw new ArgumentException(
                $"BusinessEventKey is required for {request.TemplateCode} -- without it a retry would notify the same person twice.")
            : request.BusinessEventKey.Trim();

    private static string ChannelKey(IReadOnlyCollection<NotificationChannel>? channels)
        => channels is null || channels.Count == 0
            ? string.Empty
            : string.Join(',', channels.Select(c => (int)c).OrderBy(c => c));
}
