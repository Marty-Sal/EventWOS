using EventWOS.Application.Interfaces;
using EventWOS.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.VendorAllocations.Internal;

/// <summary>
/// Async helper shared by <c>VendorAssignCrewHandler</c> and
/// <c>VendorAssignGroupHandler</c>. Pulled into its own class so the
/// behaviour (and the "legacy fallback" rule about shifts with no
/// allocations at all) lives in exactly one place. Step 3 of Phase C.
/// </summary>
internal static class VendorQuotaChecker
{
    /// <summary>
    /// Decide whether <paramref name="vendorId"/> can place one more
    /// crew member on <paramref name="shiftId"/>. See
    /// <see cref="VendorQuotaCheck"/> for the four possible outcomes.
    ///
    /// Counts active seats via the standard
    /// <see cref="AssignmentCapacityRules.OccupiesSeatOnShift"/> predicate —
    /// status-filtered, so re-invites/rejections free quota automatically.
    /// </summary>
    public static async Task<VendorQuotaCheckResult> CheckAsync(
        IAppDbContext db, Guid shiftId, Guid vendorId, CancellationToken ct)
    {
        // First: does this shift use allocations at all? If not, legacy
        // fallback — Phase B events have NO allocations and would
        // otherwise be hard-blocked here.
        var shiftHasAnyAllocation = await db.VendorShiftAllocations
            .AnyAsync(a => a.ShiftId == shiftId, ct);
        if (!shiftHasAnyAllocation)
            return new VendorQuotaCheckResult(VendorQuotaCheck.NotEnforcedYet, 0, 0);

        // Shift has opted in to allocation-managed staffing. Vendor MUST
        // have one to add crew.
        var allocation = await db.VendorShiftAllocations
            .Where(a => a.ShiftId == shiftId && a.VendorId == vendorId)
            .Select(a => new { a.Quota })
            .FirstOrDefaultAsync(ct);
        if (allocation is null)
            return new VendorQuotaCheckResult(VendorQuotaCheck.NoAllocation, 0, 0);

        // How many seats does this vendor currently occupy on this shift?
        // Same status-filtered predicate the seat math uses everywhere
        // else — re-invites and rejections "free quota" automatically
        // without us touching this code path.
        var currentlyAssigned = await db.EventAssignments
            .Where(AssignmentCapacityRules.OccupiesSeatOnShift(shiftId))
            .CountAsync(a => a.VendorId == vendorId, ct);

        return currentlyAssigned >= allocation.Quota
            ? new VendorQuotaCheckResult(VendorQuotaCheck.QuotaExhausted, allocation.Quota, currentlyAssigned)
            : new VendorQuotaCheckResult(VendorQuotaCheck.Allowed,        allocation.Quota, currentlyAssigned);
    }
}
