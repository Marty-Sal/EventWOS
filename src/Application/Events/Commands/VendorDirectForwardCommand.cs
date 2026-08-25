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

/// <summary>
/// Vendor directly forwards an Invited crew member to Manager approval,
/// bypassing the crew acceptance step. Useful when vendor already has 
/// confirmation offline or needs to proceed without waiting for crew to respond.
/// </summary>
public sealed record VendorDirectForwardCommand(
    Guid AssignmentId,
    Guid VendorUserId
) : IRequest<Result>;

public sealed class VendorDirectForwardHandler : IRequestHandler<VendorDirectForwardCommand, Result>
{
    private readonly IAppDbContext       _db;
    private readonly IUnitOfWork         _uow;
    private readonly INotificationPusher _push;
    private readonly INotificationDispatcher _notifications;
    private readonly AppUrlOptions _appUrls;

    public VendorDirectForwardHandler(
        IAppDbContext db, IUnitOfWork uow, INotificationPusher push,
        INotificationDispatcher notifications, IOptions<AppUrlOptions> appUrls)
    {
        _db   = db;
        _uow  = uow;
        _push = push;
        _notifications = notifications;
        _appUrls = appUrls.Value;
    }

    public async Task<Result> Handle(VendorDirectForwardCommand req, CancellationToken ct)
    {
        var assignment = await _db.EventAssignments
            .Include(a => a.Crew)
            // Vendor and Event come along so the managers' queue notification can say
            // who forwarded whom, for which event -- the same shape the normal vendor
            // review path sends, so the two are indistinguishable in the inbox.
            .Include(a => a.Vendor)
            .Include(a => a.Event)
            .FirstOrDefaultAsync(
                a => a.Id == req.AssignmentId && a.VendorId == req.VendorUserId, ct);

        if (assignment is null)
            return Result.Failure(new Error("Assignment.NotFound", "Assignment not found or not yours."));

        try
        {
            // Accept on behalf of crew, then immediately forward to manager
            assignment.VendorDirectForward();
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure(new Error("Assignment.InvalidTransition", ex.Message));
        }

        // Managers get a durable queue item, exactly as they do from the ordinary
        // VendorReviewAssignment path -- a bypassed crew acceptance must not also mean a
        // bypassed approval queue notification, or forwarded crew quietly sit unapproved.
        var managers = await _db.Users
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
            // Same key shape as the normal review path, deliberately: a vendor who
            // forwards and then re-runs the action cannot nag every manager twice, and
            // each manager keeps an independently idempotent row.
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

        // The crew member is deliberately told nothing here, matching the ordinary
        // vendor review stage: this only moves them into the manager's queue, and the
        // single message they get is CREW_ASSIGNMENT_APPROVED once a manager actually
        // confirms. Announcing an intermediate step invites "am I on or not?".

        await _uow.SaveChangesAsync(ct);

        // Notify crew that vendor forwarded them (they were bypassed)
        if (assignment.CrewId.HasValue)
        {
            await _push.PushToUserAsync(assignment.CrewId.Value, "VendorApprovedYou", new
        {
            assignmentId = assignment.Id,
            crewName     = assignment.Crew?.FullName ?? "Crew"
        }, ct);
        }

        // Notify all managers about new item in approval queue
        await _push.PushToRoleAsync("manager", "PendingManagerApproval", new
        {
            assignmentId = assignment.Id,
            crewName     = assignment.Crew?.FullName ?? "Crew"
        }, ct);

        return Result.Success();
    }
}
