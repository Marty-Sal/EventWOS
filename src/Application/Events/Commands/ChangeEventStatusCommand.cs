using EventOpsOracle.Application.Interfaces;
using EventOpsOracle.Application.Notifications.Abstractions;
using EventOpsOracle.Application.Notifications.Contracts;
using EventOpsOracle.Domain.Enums;
using EventOpsOracle.Domain.Interfaces;
using EventOpsOracle.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventOpsOracle.Application.Events.Commands;

public sealed record ChangeEventStatusCommand(
    Guid EventId,
    string Action,
    string? Reason = null,
    /// <summary>Who performed the action, so they are not notified about their own change.</summary>
    Guid? ActorUserId = null) : IRequest<Result>;

public sealed class ChangeEventStatusHandler : IRequestHandler<ChangeEventStatusCommand, Result>
{
    private readonly IAppDbContext _db;
    private readonly IUnitOfWork   _uow;
    private readonly INotificationDispatcher _notifications;

    public ChangeEventStatusHandler(IAppDbContext db, IUnitOfWork uow, INotificationDispatcher notifications)
    {
        _db            = db;
        _uow           = uow;
        _notifications = notifications;
    }

    public async Task<Result> Handle(ChangeEventStatusCommand req, CancellationToken ct)
    {
        var ev = await _db.Events.FirstOrDefaultAsync(e => e.Id == req.EventId, ct);
        if (ev is null) return Result.Failure(new Error("Event.NotFound", "Event not found."));

        try
        {
            switch (req.Action.ToLower())
            {
                case "publish":   ev.Publish();             break;
                case "start":     ev.Start();               break;
                case "complete":  ev.Complete();            break;
                case "cancel":    ev.Cancel(req.Reason);    break;
                default: return Result.Failure(new Error("Event.InvalidAction", $"Unknown action: {req.Action}"));
            }
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure(new Error("Event.InvalidTransition", ex.Message));
        }

        // Cancelling an event told NOBODY before this. Crew and vendors found out by
        // turning up to a venue for a job that no longer existed -- the single most
        // expensive silence in the system, which is why it is the first fan-out.
        //
        // Fan-out rather than a loop: one outbox row now, recipients resolved by
        // the worker afterwards, so cancelling a 500-crew event does not hold a
        // long transaction open inside an admin's HTTP request.
        if (req.Action.Equals("cancel", StringComparison.OrdinalIgnoreCase))
        {
            _notifications.EnqueueFanOut(new NotificationFanOutRequest(
                NotificationTemplateCodes.EventCancelled,
                NotificationAudience.EventCrewAndVendors,
                EventId: ev.Id,
                // Keyed on the event and the transition, not on time, so a retried
                // request cannot announce the same cancellation twice.
                BusinessEventKey: $"event:{ev.Id}:cancelled",
                Data: new Dictionary<string, string?>
                {
                    [NotificationTokens.EventName] = ev.Title,
                    [NotificationTokens.EventDate] = ev.StartAt.ToString("dd MMM yyyy"),
                    [NotificationTokens.EventTime] = ev.StartAt.ToString("HH:mm"),
                    [NotificationTokens.Reason]    = string.IsNullOrWhiteSpace(req.Reason)
                        ? "No reason given"
                        : req.Reason
                },
                ActorUserId: req.ActorUserId,
                // High: someone is about to travel to a venue for nothing.
                Priority: NotificationPriority.High,
                ExcludeUserIds: req.ActorUserId is { } actor ? new[] { actor } : null));
        }

        await _uow.SaveChangesAsync(ct);
        return Result.Success();
    }
}
