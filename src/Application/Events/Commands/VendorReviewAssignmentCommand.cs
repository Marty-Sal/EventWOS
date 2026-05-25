using EventWOS.Application.Interfaces;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Events.Commands;

// ── Vendor Approve ────────────────────────────────────────────────────────────

/// <summary>Vendor approves a crew member → forwards to Manager approval queue.</summary>
public sealed record VendorApproveAssignmentCommand(
    Guid AssignmentId,
    Guid VendorUserId
) : IRequest<Result>;

public sealed class VendorApproveAssignmentHandler
    : IRequestHandler<VendorApproveAssignmentCommand, Result>
{
    private readonly IAppDbContext _db;
    private readonly IUnitOfWork   _uow;
    public VendorApproveAssignmentHandler(IAppDbContext db, IUnitOfWork uow) { _db = db; _uow = uow; }

    public async Task<Result> Handle(VendorApproveAssignmentCommand req, CancellationToken ct)
    {
        var assignment = await _db.EventAssignments
            .FirstOrDefaultAsync(a => a.Id == req.AssignmentId && a.VendorId == req.VendorUserId, ct);

        if (assignment is null)
            return Result.Failure(new Error("Assignment.NotFound", "Assignment not found or not yours."));

        try   { assignment.VendorApprove(); }
        catch (InvalidOperationException ex)
        { return Result.Failure(new Error("Assignment.InvalidTransition", ex.Message)); }

        await _uow.SaveChangesAsync(ct);
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
    private readonly IAppDbContext _db;
    private readonly IUnitOfWork   _uow;
    public VendorRejectAssignmentHandler(IAppDbContext db, IUnitOfWork uow) { _db = db; _uow = uow; }

    public async Task<Result> Handle(VendorRejectAssignmentCommand req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Reason))
            return Result.Failure(new Error("Assignment.ReasonRequired", "Rejection reason is mandatory."));

        var assignment = await _db.EventAssignments
            .FirstOrDefaultAsync(a => a.Id == req.AssignmentId && a.VendorId == req.VendorUserId, ct);

        if (assignment is null)
            return Result.Failure(new Error("Assignment.NotFound", "Assignment not found or not yours."));

        try   { assignment.VendorReject(req.VendorUserId, req.Reason); }
        catch (InvalidOperationException ex)
        { return Result.Failure(new Error("Assignment.InvalidTransition", ex.Message)); }

        await _uow.SaveChangesAsync(ct);
        return Result.Success();
    }
}
