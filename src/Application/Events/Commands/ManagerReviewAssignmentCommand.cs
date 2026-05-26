using EventWOS.Application.Interfaces;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Events.Commands;

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
    public ManagerApproveAssignmentHandler(IAppDbContext db, IUnitOfWork uow, INotificationPusher push) { _db = db; _uow = uow; _push = push; }

    public async Task<Result> Handle(ManagerApproveAssignmentCommand req, CancellationToken ct)
    {
        var assignment = await _db.EventAssignments
            .Include(a => a.Crew)
            .FirstOrDefaultAsync(a => a.Id == req.AssignmentId, ct);

        if (assignment is null)
            return Result.Failure(new Error("Assignment.NotFound", "Assignment not found."));

        try   { assignment.ManagerApprove(); }
        catch (InvalidOperationException ex)
        { return Result.Failure(new Error("Assignment.InvalidTransition", ex.Message)); }

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
    public ManagerRejectAssignmentHandler(IAppDbContext db, IUnitOfWork uow, INotificationPusher push) { _db = db; _uow = uow; _push = push; }

    public async Task<Result> Handle(ManagerRejectAssignmentCommand req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Reason))
            return Result.Failure(new Error("Assignment.ReasonRequired", "Rejection reason is mandatory."));

        var assignment = await _db.EventAssignments
            .Include(a => a.Crew)
            .FirstOrDefaultAsync(a => a.Id == req.AssignmentId, ct);

        if (assignment is null)
            return Result.Failure(new Error("Assignment.NotFound", "Assignment not found."));

        try   { assignment.ManagerReject(req.ManagerUserId, req.Reason); }
        catch (InvalidOperationException ex)
        { return Result.Failure(new Error("Assignment.InvalidTransition", ex.Message)); }

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
