using EventOpsOracle.Application.Common;
using EventOpsOracle.Application.Interfaces;
using EventOpsOracle.Application.Notifications.Abstractions;
using EventOpsOracle.Application.Notifications.Contracts;
using EventOpsOracle.Domain.Enums;
using EventOpsOracle.Domain.Interfaces;
using EventOpsOracle.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EventOpsOracle.Application.Events.Commands;

// ── Vendor Approve ────────────────────────────────────────────────────────────

/// <summary>Vendor approves a crew member → forwards to Manager approval queue.</summary>
public sealed record VendorApproveAssignmentCommand(
    Guid AssignmentId,
    Guid VendorUserId
) : IRequest<Result>;

public sealed class VendorApproveAssignmentHandler
    : IRequestHandler<VendorApproveAssignmentCommand, Result>
{
    private readonly IAppDbContext     _db;
    private readonly IUnitOfWork       _uow;
    private readonly INotificationPusher _push;
    private readonly INotificationDispatcher _notifications;
    private readonly AppUrlOptions _appUrls;
    public VendorApproveAssignmentHandler(
        IAppDbContext db, IUnitOfWork uow, INotificationPusher push,
        INotificationDispatcher notifications, IOptions<AppUrlOptions> appUrls)
    { _db = db; _uow = uow; _push = push; _notifications = notifications; _appUrls = appUrls.Value; }

    public async Task<Result> Handle(VendorApproveAssignmentCommand req, CancellationToken ct)
    {
        var assignment = await _db.EventAssignments
            .Include(a => a.Crew)
            .Include(a => a.Event)
            .Include(a => a.Vendor)
            .FirstOrDefaultAsync(a => a.Id == req.AssignmentId && a.VendorId == req.VendorUserId, ct);

        if (assignment is null)
            return Result.Failure(new Error("Assignment.NotFound", "Assignment not found or not yours."));

        try   { assignment.VendorApprove(); }
        catch (InvalidOperationException ex)
        { return Result.Failure(new Error("Assignment.InvalidTransition", ex.Message)); }

        // The CREW member is deliberately NOT told "approved" here, even though this
        // is the obvious place to do it. VendorApprove() moves the row to
        // PendingManagerApproval, not to approved -- a manager can still reject it.
        // Sending "your assignment is confirmed" at this point means some crew turn
        // up to an event they were later rejected from, which is worse than silence.
        // CREW_ASSIGNMENT_APPROVED is sent by ManagerApproveAssignmentHandler, where
        // the decision is actually final. The transient toast below still fires, so
        // a crew member watching the screen sees progress.
        //
        // What DOES need a durable message is the manager queue: the only signal was
        // a role-wide SignalR push, so approvals piled up unseen while the crew
        // member waited to find out whether they had a shift at all.
        var managers = await _db.Users
            .AsNoTracking()
            .Where(u => (u.Role == UserRole.Manager || u.Role == UserRole.Admin)
                     && u.Status == UserStatus.Active
                     && !u.IsDeleted)
            .Select(u => new { u.Id, u.FullName })
            .ToListAsync(ct);

        var reviewLink = _appUrls.BaseUrl.TrimEnd('/') + "/manager-approvals";
        var crewName   = assignment.Crew?.FullName ?? "A crew member";

        _notifications.Enqueue(managers.Select(m => new NotificationRequest(
            NotificationTemplateCodes.AssignmentPendingApproval,
            RecipientUserId: m.Id,
            // Keyed on the assignment AND the recipient: re-running a vendor approval
            // cannot nag the same manager twice, but each manager still gets their own
            // independently idempotent row.
            BusinessEventKey: $"assignment:{assignment.Id}:pending-manager:{m.Id}",
            Data: new Dictionary<string, string?>
            {
                [NotificationTokens.RecipientName] = m.FullName,
                [NotificationTokens.CrewName]      = crewName,
                [NotificationTokens.VendorName]    = assignment.Vendor?.FullName ?? "their vendor",
                [NotificationTokens.EventName]     = assignment.Event?.Title ?? "an event",
                [NotificationTokens.EventDate]     = assignment.Event?.StartAt.ToString("dd MMM yyyy") ?? "-",
                [NotificationTokens.Link]          = reviewLink
            },
            ActorUserId: req.VendorUserId)));

        await _uow.SaveChangesAsync(ct);

        // Notify crew of vendor approval
        if (assignment.CrewId.HasValue)
        {
            await _push.PushToUserAsync(assignment.CrewId.Value, "VendorApprovedYou", new
        {
            assignmentId = assignment.Id
        }, ct);
        }

        // Notify all managers about pending approval
        await _push.PushToRoleAsync("manager", "PendingManagerApproval", new
        {
            assignmentId = assignment.Id,
            crewName     = assignment.Crew?.FullName ?? "Crew"
        }, ct);

        return Result.Success();
    }
}

// ── Vendor Reject ─────────────────────────────────────────────────────────────

/// <summary>Vendor rejects a crew member with a mandatory reason.</summary>
public sealed record VendorRejectAssignmentCommand(
    Guid   AssignmentId,
    Guid   VendorUserId,
    string Reason
) : IRequest<Result>;

public sealed class VendorRejectAssignmentHandler
    : IRequestHandler<VendorRejectAssignmentCommand, Result>
{
    private readonly IAppDbContext     _db;
    private readonly IUnitOfWork       _uow;
    private readonly INotificationPusher _push;
    private readonly INotificationDispatcher _notifications;
    public VendorRejectAssignmentHandler(
        IAppDbContext db, IUnitOfWork uow, INotificationPusher push,
        INotificationDispatcher notifications)
    { _db = db; _uow = uow; _push = push; _notifications = notifications; }

    public async Task<Result> Handle(VendorRejectAssignmentCommand req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Reason))
            return Result.Failure(new Error("Assignment.ReasonRequired", "Rejection reason is mandatory."));

        var assignment = await _db.EventAssignments
            .Include(a => a.Crew)
            .Include(a => a.Event)
            .FirstOrDefaultAsync(a => a.Id == req.AssignmentId && a.VendorId == req.VendorUserId, ct);

        if (assignment is null)
            return Result.Failure(new Error("Assignment.NotFound", "Assignment not found or not yours."));

        try   { assignment.VendorReject(req.VendorUserId, req.Reason); }
        catch (InvalidOperationException ex)
        { return Result.Failure(new Error("Assignment.InvalidTransition", ex.Message)); }

        // Unlike approval, rejection by the vendor IS final -- RejectedByVendor is a
        // terminal state with no manager stage after it, so the crew member can be
        // told now. This is the message worth persisting most: somebody who thinks
        // they are working on Saturday needs to find out that they are not, whether
        // or not they had the tab open when the vendor clicked.
        if (assignment.CrewId.HasValue)
        {
            _notifications.Enqueue(new NotificationRequest(
                NotificationTemplateCodes.CrewAssignmentRejected,
                RecipientUserId: assignment.CrewId.Value,
                BusinessEventKey: $"assignment:{assignment.Id}:rejected-by-vendor",
                Data: new Dictionary<string, string?>
                {
                    [NotificationTokens.RecipientName] = assignment.Crew?.FullName ?? "there",
                    [NotificationTokens.EventName]     = assignment.Event?.Title ?? "the event",
                    // Reason is mandatory above, so this token is always populated --
                    // a rejection with no stated reason just produces another support
                    // conversation.
                    [NotificationTokens.Reason]        = req.Reason
                },
                ActorUserId: req.VendorUserId));
        }

        await _uow.SaveChangesAsync(ct);

        // Notify crew of rejection
        if (assignment.CrewId.HasValue)
        {
            await _push.PushToUserAsync(assignment.CrewId.Value, "VendorRejectedYou", new
        {
            assignmentId = assignment.Id,
            reason       = req.Reason
        }, ct);
        }

        return Result.Success();
    }
}
