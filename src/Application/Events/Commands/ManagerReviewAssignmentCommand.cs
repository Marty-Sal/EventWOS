using EventOpsOracle.Application.Interfaces;
using EventOpsOracle.Application.Notifications.Abstractions;
using EventOpsOracle.Application.Notifications.Contracts;
using EventOpsOracle.Domain.Interfaces;
using EventOpsOracle.Domain.Rules;
using EventOpsOracle.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventOpsOracle.Application.Events.Commands;

// ── Manager Approve ───────────────────────────────────────────────────────────

/// <summary>Manager gives final approval — crew is now fully confirmed.</summary>
public sealed record ManagerApproveAssignmentCommand(
    Guid AssignmentId,
    Guid ManagerUserId
) : IRequest<Result>;

public sealed class ManagerApproveAssignmentHandler
    : IRequestHandler<ManagerApproveAssignmentCommand, Result>
{
    private readonly IAppDbContext     _db;
    private readonly IUnitOfWork       _uow;
    private readonly INotificationPusher _push;
    private readonly INotificationDispatcher _notifications;
    public ManagerApproveAssignmentHandler(
        IAppDbContext db, IUnitOfWork uow, INotificationPusher push,
        INotificationDispatcher notifications)
    { _db = db; _uow = uow; _push = push; _notifications = notifications; }

    public async Task<Result> Handle(ManagerApproveAssignmentCommand req, CancellationToken ct)
    {
        var assignment = await _db.EventAssignments
            .Include(a => a.Crew)
            .Include(a => a.Event)
            .FirstOrDefaultAsync(a => a.Id == req.AssignmentId, ct);

        if (assignment is null)
            return Result.Failure(new Error("Assignment.NotFound", "Assignment not found."));

        // Capacity check — approving this row will (typically) turn it into a
        // seat-occupier. Reject when the cap would be exceeded so we don't
        // silently over-staff the event. MaxCrew == 0 means "unlimited".
        if (assignment.Event is not null && assignment.Event.MaxCrew > 0)
        {
            var occupiedExcludingThis = await _db.EventAssignments
                .Where(a => a.EventId == assignment.EventId && a.Id != assignment.Id)
                .Where(AssignmentCapacityRules.OccupiesSeat)
                .CountAsync(ct);

            if (occupiedExcludingThis + 1 > assignment.Event.MaxCrew)
            {
                return Result.Failure(new Error(
                    "Assignment.MaxCrewReached",
                    $"Staff cap reached ({assignment.Event.MaxCrew} approved). " +
                    "Update the event to add more slots before approving more crew."));
            }
        }

        try   { assignment.ManagerApprove(); }
        catch (InvalidOperationException ex)
        { return Result.Failure(new Error("Assignment.InvalidTransition", ex.Message)); }

        // THIS is where a crew member is actually confirmed. ManagerApproved is the
        // end of the two-stage flow, so CREW_ASSIGNMENT_APPROVED is sent here and
        // nowhere earlier -- the vendor stage only forwards the row to this queue.
        //
        // It is also the notification people plan their week around: if it is lost,
        // somebody either misses a shift they were confirmed for or turns up to one
        // they were not. A toast that requires the tab to be open is not good enough
        // for that, which is the whole reason this platform exists.
        if (assignment.CrewId.HasValue)
        {
            _notifications.Enqueue(new NotificationRequest(
                NotificationTemplateCodes.CrewAssignmentApproved,
                RecipientUserId: assignment.CrewId.Value,
                BusinessEventKey: $"assignment:{assignment.Id}:manager-approved",
                Data: new Dictionary<string, string?>
                {
                    [NotificationTokens.RecipientName] = assignment.Crew?.FullName ?? "there",
                    [NotificationTokens.EventName]     = assignment.Event?.Title ?? "the event",
                    [NotificationTokens.EventDate]     = assignment.Event?.StartAt.ToString("dd MMM yyyy") ?? "-",
                    [NotificationTokens.EventTime]     = assignment.Event?.StartAt.ToString("HH:mm") ?? "-"
                },
                ActorUserId: req.ManagerUserId));
        }

        // The VENDOR keeps only the transient push. They are notified because their
        // roster view changes, not because they need to act -- they already approved
        // this person, so an email telling them a manager agreed is noise in a mailbox
        // that will carry one of these per crew member per event.

        await _uow.SaveChangesAsync(ct);

        // Notify crew of final approval
        if (assignment.CrewId.HasValue)
        {
            await _push.PushToUserAsync(assignment.CrewId.Value, "ManagerApprovedYou", new
        {
            assignmentId = assignment.Id
        }, ct);
        }

        // Notify vendor too
        if (assignment.VendorId.HasValue)
        {
            await _push.PushToUserAsync(assignment.VendorId.Value, "ManagerApprovedYou_ForCrewMember", new
        {
            assignmentId = assignment.Id,
            crewName     = assignment.Crew?.FullName ?? "Crew"
        }, ct);
        }

        return Result.Success();
    }
}

// ── Manager Reject ────────────────────────────────────────────────────────────

/// <summary>Manager rejects in final review with a mandatory reason.</summary>
public sealed record ManagerRejectAssignmentCommand(
    Guid   AssignmentId,
    Guid   ManagerUserId,
    string Reason
) : IRequest<Result>;

public sealed class ManagerRejectAssignmentHandler
    : IRequestHandler<ManagerRejectAssignmentCommand, Result>
{
    private readonly IAppDbContext     _db;
    private readonly IUnitOfWork       _uow;
    private readonly INotificationPusher _push;
    private readonly INotificationDispatcher _notifications;
    public ManagerRejectAssignmentHandler(
        IAppDbContext db, IUnitOfWork uow, INotificationPusher push,
        INotificationDispatcher notifications)
    { _db = db; _uow = uow; _push = push; _notifications = notifications; }

    public async Task<Result> Handle(ManagerRejectAssignmentCommand req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Reason))
            return Result.Failure(new Error("Assignment.ReasonRequired", "Rejection reason is mandatory."));

        var assignment = await _db.EventAssignments
            .Include(a => a.Crew)
            // Event comes along for the notification: "your assignment for the event
            // was not approved" is not a message anyone can act on.
            .Include(a => a.Event)
            .FirstOrDefaultAsync(a => a.Id == req.AssignmentId, ct);

        if (assignment is null)
            return Result.Failure(new Error("Assignment.NotFound", "Assignment not found."));

        try   { assignment.ManagerReject(req.ManagerUserId, req.Reason); }
        catch (InvalidOperationException ex)
        { return Result.Failure(new Error("Assignment.InvalidTransition", ex.Message)); }

        // Final rejection. The crew member may well have been told by the vendor that
        // they were through, so this reversal has to reach them: this is the message
        // that stops someone travelling to an event they are no longer on.
        if (assignment.CrewId.HasValue)
        {
            _notifications.Enqueue(new NotificationRequest(
                NotificationTemplateCodes.CrewAssignmentRejected,
                RecipientUserId: assignment.CrewId.Value,
                // Distinct from the vendor-stage key so the two rejection stages can
                // never be de-duplicated into each other.
                BusinessEventKey: $"assignment:{assignment.Id}:manager-rejected",
                Data: new Dictionary<string, string?>
                {
                    [NotificationTokens.RecipientName] = assignment.Crew?.FullName ?? "there",
                    [NotificationTokens.EventName]     = assignment.Event?.Title ?? "the event",
                    [NotificationTokens.Reason]        = req.Reason
                },
                ActorUserId: req.ManagerUserId));
        }

        await _uow.SaveChangesAsync(ct);

        // Notify crew of rejection
        if (assignment.CrewId.HasValue)
        {
            await _push.PushToUserAsync(assignment.CrewId.Value, "ManagerRejectedYou", new
        {
            assignmentId = assignment.Id,
            reason       = req.Reason
        }, ct);
        }

        // Notify vendor too
        if (assignment.VendorId.HasValue)
        {
            await _push.PushToUserAsync(assignment.VendorId.Value, "ManagerRejectedYou_ForCrewMember", new
        {
            assignmentId = assignment.Id,
            crewName     = assignment.Crew?.FullName ?? "Crew",
            reason       = req.Reason
        }, ct);
        }

        return Result.Success();
    }
}
