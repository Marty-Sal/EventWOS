using EventOpsOracle.Shared.Result;
using EventOpsOracle.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EventOpsOracle.Application.Notifications.Commands;

/// <summary>What a provider is telling us happened to a message.</summary>
public enum ProviderDeliveryEventType
{
    /// <summary>Reached the recipient's inbox or handset.</summary>
    Delivered,

    /// <summary>Opened or read. Further along than Delivered.</summary>
    Read,

    /// <summary>Will never arrive: bounce, block, invalid number, spam report.</summary>
    Failed,

    /// <summary>Provider is still trying (SendGrid deferral, WhatsApp pending). Informational.</summary>
    Deferred,

    /// <summary>Recognised payload, event we do not act on (open tracking, unsubscribe...).</summary>
    Ignored
}

/// <summary>
/// One normalised provider event. Providers disagree about almost everything, so
/// each webhook parser reduces its own payload to this shape and the handler
/// stays free of provider quirks.
/// </summary>
public sealed record ProviderDeliveryEvent(
    Guid? DeliveryId,
    string? ProviderMessageId,
    ProviderDeliveryEventType Type,
    string? Detail,
    DateTime OccurredAt);

/// <summary>
/// Applies provider callbacks to delivery rows. This is what turns Accepted into
/// Delivered or Failed -- without it every email and WhatsApp message sits at
/// "handed to the provider" forever and the audit trail can never answer whether
/// the crew member actually got it.
/// </summary>
public sealed record RecordProviderDeliveryEventsCommand(
    string Provider,
    IReadOnlyList<ProviderDeliveryEvent> Events) : IRequest<Result<int>>;

public sealed class RecordProviderDeliveryEventsHandler
    : IRequestHandler<RecordProviderDeliveryEventsCommand, Result<int>>
{
    private readonly IAppDbContext _db;
    private readonly ILogger<RecordProviderDeliveryEventsHandler> _logger;

    public RecordProviderDeliveryEventsHandler(
        IAppDbContext db, ILogger<RecordProviderDeliveryEventsHandler> logger)
    {
        _db     = db;
        _logger = logger;
    }

    public async Task<Result<int>> Handle(RecordProviderDeliveryEventsCommand request, CancellationToken ct)
    {
        var events = request.Events
            .Where(e => e.Type != ProviderDeliveryEventType.Ignored)
            // Oldest first. Providers batch and reorder freely, and applying a
            // stale "delivered" after a "read" would walk the row backwards.
            .OrderBy(e => e.OccurredAt)
            .ToList();

        if (events.Count == 0) return Result<int>.Success(0);

        var byId        = events.Where(e => e.DeliveryId is not null).Select(e => e.DeliveryId!.Value).Distinct().ToList();
        var byMessageId = events.Where(e => e.DeliveryId is null && !string.IsNullOrWhiteSpace(e.ProviderMessageId))
                                .Select(e => e.ProviderMessageId!).Distinct().ToList();

        var deliveries = await _db.NotificationDeliveries
            .Where(d => byId.Contains(d.Id) ||
                        (d.ProviderMessageId != null && byMessageId.Contains(d.ProviderMessageId)))
            .ToListAsync(ct);

        if (deliveries.Count == 0)
        {
            // Common and harmless: provider retries, events for messages sent by
            // another environment sharing the account, or tracking events for
            // mail we never queued. Logged at debug so it cannot fill the logs.
            _logger.LogDebug(
                "{Provider} webhook: no matching deliveries for {EventCount} event(s)",
                request.Provider, events.Count);

            return Result<int>.Success(0);
        }

        var lookupById        = deliveries.ToDictionary(d => d.Id);
        var lookupByMessageId = deliveries
            .Where(d => !string.IsNullOrWhiteSpace(d.ProviderMessageId))
            .GroupBy(d => d.ProviderMessageId!)
            .ToDictionary(g => g.Key, g => g.First());

        var applied = 0;

        foreach (var evt in events)
        {
            var delivery =
                evt.DeliveryId is { } id && lookupById.TryGetValue(id, out var byIdMatch) ? byIdMatch :
                evt.ProviderMessageId is { } mid && lookupByMessageId.TryGetValue(mid, out var byMidMatch) ? byMidMatch :
                null;

            if (delivery is null) continue;

            var occurredAt = evt.OccurredAt == default ? DateTime.UtcNow : evt.OccurredAt;

            switch (evt.Type)
            {
                case ProviderDeliveryEventType.Delivered:
                    // The entity refuses to regress from Read, so replayed and
                    // out-of-order webhooks are safe to apply blindly.
                    delivery.MarkDelivered(occurredAt);
                    applied++;
                    break;

                case ProviderDeliveryEventType.Read:
                    delivery.MarkRead(occurredAt);
                    applied++;
                    break;

                case ProviderDeliveryEventType.Failed:
                    // A provider-side failure is final: the message bounced, was
                    // blocked, or the number cannot receive it. Retrying our end
                    // would send it to the same dead address again.
                    delivery.MarkFailed(evt.Detail ?? $"{request.Provider} reported failure", occurredAt);
                    applied++;

                    _logger.LogWarning(
                        "{Provider} reported delivery {DeliveryId} ({Channel} to {Destination}) failed: {Detail}",
                        request.Provider, delivery.Id, delivery.Channel, delivery.Destination, evt.Detail);
                    break;

                case ProviderDeliveryEventType.Deferred:
                    // The provider is still trying. Touching the row would either
                    // duplicate the send or mark a live message dead.
                    _logger.LogInformation(
                        "{Provider} deferred delivery {DeliveryId}: {Detail}",
                        request.Provider, delivery.Id, evt.Detail);
                    break;
            }
        }

        if (applied == 0) return Result<int>.Success(0);

        // The parent's status is a rollup of ALL its deliveries, so the siblings
        // have to be loaded too -- otherwise a notification whose email bounced
        // would be reported as failed while its WhatsApp copy was still in
        // flight. Same DbContext, so these are the very rows just mutated.
        var parentIds = deliveries.Select(d => d.NotificationId).Distinct().ToList();

        var notifications = await _db.Notifications
            .Include(n => n.Deliveries)
            .Where(n => parentIds.Contains(n.Id))
            .ToListAsync(ct);

        foreach (var notification in notifications)
            notification.RecalculateStatus();

        await _db.SaveChangesAsync(ct);

        return Result<int>.Success(applied);
    }
}
