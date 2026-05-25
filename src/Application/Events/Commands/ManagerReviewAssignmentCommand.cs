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
    private readonly IAppDbContext _db;
    private readonly IUnitOfWork   _uow;
    public ManagerApproveAssignmentHandler(IAppDbContext db, IUnitOfWork uow) { _db = db; _uow = uow; }

    public async Task<Result> Handle(ManagerApproveAssignmentCommand req, CancellationToken ct)
    {
        var assignment = await _db.EventAssignments
            .FirstOrDefaultAsync(a => a.Id == req.AssignmentId, ct);

        if (assignment is null)
            return Result.Failure(new Error("Assignment.NotFound", "Assignment not found."));

        try   { assignment.ManagerApprove(); }
        catch (InvalidOperationException ex)
        { return Result.Failure(new Error("Assignment.InvalidTransition", ex.Message)); }

        await _uow.SaveChangesAsync(ct);
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
    private readonly IAppDbContext _db;
    private readonly IUnitOfWork   _uow;
    public ManagerRejectAssignmentHandler(IAppDbContext db, IUnitOfWork uow) { _db = db; _uow = uow; }

    public async Task<Result> Handle(ManagerRejectAssignmentCommand req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Reason))
            return Result.Failure(new Error("Assignment.ReasonRequired", "Rejection reason is mandatory."));

        var assignment = await _db.EventAssignments
            .FirstOrDefaultAsync(a => a.Id == req.AssignmentId, ct);

        if (assignment is null)
            return Result.Failure(new Error("Assignment.NotFound", "Assignment not found."));

        try   { assignment.ManagerReject(req.ManagerUserId, req.Reason); }
        catch (InvalidOperationException ex)
        { return Result.Failure(new Error("Assignment.InvalidTransition", ex.Message)); }

        await _uow.SaveChangesAsync(ct);
        return Result.Success();
    }
}
