using EventWOS.Application.Interfaces;
using EventWOS.Domain.Interfaces;
using EventWOS.Domain.Rules;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.VendorAllocations.Commands;

/// <summary>
/// Soft-delete an allocation. Refuses if any crew currently occupy a seat
/// under this vendor on this shift — those crew would be orphaned otherwise.
///
/// Idempotent: archiving an already-archived allocation returns success
/// (no-op). Mirrors the <see cref="ArchiveScopeOfWorkCommand"/> contract.
/// </summary>
public sealed record ArchiveVendorAllocationCommand(
    Guid AllocationId,
    Guid ActingUserId
) : IRequest<Result>;

public sealed class ArchiveVendorAllocationHandler
    : IRequestHandler<ArchiveVendorAllocationCommand, Result>
{
    private readonly IAppDbContext _db;
    private readonly IUnitOfWork   _uow;
    public ArchiveVendorAllocationHandler(IAppDbContext db, IUnitOfWork uow) { _db = db; _uow = uow; }

    public async Task<Result> Handle(ArchiveVendorAllocationCommand req, CancellationToken ct)
    {
        var alloc = await _db.VendorShiftAllocations
            .IgnoreQueryFilters()  // allow idempotent archive of already-archived
            .FirstOrDefaultAsync(a => a.Id == req.AllocationId, ct);
        if (alloc is null)
            return Result.Failure(new Error("VendorAllocation.NotFound", "Allocation not found."));

        if (alloc.IsDeleted) return Result.Success();  // idempotent

        // Count occupied seats under this vendor on this shift via the
        // same status-filtered predicate as everywhere else.
        var currentlyAssigned = await _db.EventAssignments
            .Where(AssignmentCapacityRules.OccupiesSeatOnShift(alloc.ShiftId))
            .CountAsync(a => a.VendorId == alloc.VendorId, ct);

        try
        {
            alloc.Archive(req.ActingUserId, currentlyAssigned);
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure(new Error("VendorAllocation.WouldOrphanCrew", ex.Message));
        }

        await _uow.SaveChangesAsync(ct);
        return Result.Success();
    }
}
