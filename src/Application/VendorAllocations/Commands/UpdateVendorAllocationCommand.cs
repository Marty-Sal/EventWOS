using EventWOS.Application.Interfaces;
using EventWOS.Application.VendorAllocations.DTOs;
using EventWOS.Domain.Interfaces;
using EventWOS.Domain.Rules;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.VendorAllocations.Commands;

/// <summary>
/// Change the quota on an existing allocation.
///
/// Three checks, in order:
///   1. Allocation exists and is active (archived rows must be restored first
///      — though restore-from-archive isn't a Phase C affordance; admins can
///      just create a new allocation since the filter unique index will
///      have freed the slot. So "archived" here is a hard error.)
///   2. New quota >= count of crew this vendor currently occupies on the
///      shift. <see cref="VendorShiftAllocation.UpdateQuota"/> enforces this
///      but we surface a friendly error message; we count via the same
///      <c>OccupiesSeatOnShift</c> predicate that the seat math uses.
///   3. SUM(other allocations on shift) + new quota &lt;= shift capacity.
///      This subtracts the current row's quota before adding the new one
///      so growing your own allocation only counts the *delta* against
///      the shift's headroom.
/// </summary>
public sealed record UpdateVendorAllocationCommand(
    Guid AllocationId,
    int  NewQuota,
    Guid ActingUserId
) : IRequest<Result<VendorShiftAllocationDto>>;

public sealed class UpdateVendorAllocationHandler
    : IRequestHandler<UpdateVendorAllocationCommand, Result<VendorShiftAllocationDto>>
{
    private readonly IAppDbContext _db;
    private readonly IUnitOfWork   _uow;
    public UpdateVendorAllocationHandler(IAppDbContext db, IUnitOfWork uow) { _db = db; _uow = uow; }

    public async Task<Result<VendorShiftAllocationDto>> Handle(
        UpdateVendorAllocationCommand req, CancellationToken ct)
    {
        var alloc = await _db.VendorShiftAllocations
            .Include(a => a.Shift)!.ThenInclude(s => s!.ScopeOfWork)
            .Include(a => a.Vendor)
            .FirstOrDefaultAsync(a => a.Id == req.AllocationId, ct);

        if (alloc is null)
            return Result.Failure<VendorShiftAllocationDto>(new Error(
                "VendorAllocation.NotFound", "Allocation not found."));

        if (alloc.Shift is null)
            // Should be impossible — FK guarantees this. Belt-and-braces
            // in case the shift was hard-deleted via SQL.
            return Result.Failure<VendorShiftAllocationDto>(new Error(
                "VendorAllocation.ShiftMissing",
                "Allocation's shift no longer exists. Recreate the shift first."));

        // Count current occupied seats by this vendor on this shift.
        // OccupiesSeatOnShift filters by status (not row existence) so
        // rejections / re-invites work without us touching this count.
        var currentlyAssigned = await _db.EventAssignments
            .Where(AssignmentCapacityRules.OccupiesSeatOnShift(alloc.ShiftId))
            .CountAsync(a => a.VendorId == alloc.VendorId, ct);

        // Shift-wide headroom: total allocated on the shift, MINUS this
        // allocation's existing quota (because it's about to change),
        // PLUS the new quota — must fit in CrewCount.
        var otherAllocations = await _db.VendorShiftAllocations
            .Where(a => a.ShiftId == alloc.ShiftId && a.Id != alloc.Id)
            .SumAsync(a => (int?)a.Quota, ct) ?? 0;

        if (otherAllocations + req.NewQuota > alloc.Shift.CrewCount)
            return Result.Failure<VendorShiftAllocationDto>(new Error(
                "VendorAllocation.OverCommitsShift",
                $"Quota of {req.NewQuota} over-commits the shift. " +
                $"Shift capacity is {alloc.Shift.CrewCount}, " +
                $"already allocated to other vendors: {otherAllocations}. " +
                $"Max for this vendor: {alloc.Shift.CrewCount - otherAllocations}."));

        try
        {
            alloc.UpdateQuota(req.NewQuota, currentlyAssigned);
        }
        catch (ArgumentException ex)
        {
            return Result.Failure<VendorShiftAllocationDto>(new Error(
                "VendorAllocation.Invalid", ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            // Shrink-floor violation — domain message is already clear.
            return Result.Failure<VendorShiftAllocationDto>(new Error(
                "VendorAllocation.WouldOrphanCrew", ex.Message));
        }

        await _uow.SaveChangesAsync(ct);

        return Result.Success(new VendorShiftAllocationDto(
            alloc.Id,
            alloc.ShiftId, alloc.VendorId,
            alloc.Vendor?.FullName ?? alloc.Vendor?.Mobile ?? "(unnamed vendor)",
            alloc.Shift.EventId,
            alloc.Shift.ScopeOfWorkId,
            alloc.Shift.ScopeOfWork?.Name ?? "(unknown)",
            alloc.Quota,
            currentlyAssigned,
            alloc.IsDeleted, alloc.CreatedAt, alloc.UpdatedAt));
    }
}
