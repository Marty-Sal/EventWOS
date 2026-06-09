using EventWOS.Application.Interfaces;
using EventWOS.Application.VendorAllocations.DTOs;
using EventWOS.Domain.Rules;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.VendorAllocations.Queries;

/// <summary>
/// List allocations on a specific shift. Used by:
///   • Admin allocation table in the event detail drawer.
///   • Capacity-check tooltip ("12 capacity, 7 allocated to vendors").
///
/// Returns all rows including archived (caller filters in UI). Each row's
/// <see cref="VendorShiftAllocationDto.CurrentlyAssigned"/> is computed
/// here in a single grouped query so the UI doesn't N+1.
/// </summary>
public sealed record GetVendorAllocationsForShiftQuery(
    Guid ShiftId
) : IRequest<Result<IReadOnlyList<VendorShiftAllocationDto>>>;

public sealed class GetVendorAllocationsForShiftHandler
    : IRequestHandler<GetVendorAllocationsForShiftQuery, Result<IReadOnlyList<VendorShiftAllocationDto>>>
{
    private readonly IAppDbContext _db;
    public GetVendorAllocationsForShiftHandler(IAppDbContext db) => _db = db;

    public async Task<Result<IReadOnlyList<VendorShiftAllocationDto>>> Handle(
        GetVendorAllocationsForShiftQuery req, CancellationToken ct)
    {
        // Pull allocations + their shift's scope name and vendor name in
        // one EF round-trip. Archived rows are surfaced so the admin can
        // see the audit trail; UI hides them behind a toggle.
        var rows = await _db.VendorShiftAllocations
            .IgnoreQueryFilters()
            .Where(a => a.ShiftId == req.ShiftId)
            .Include(a => a.Shift)!.ThenInclude(s => s!.ScopeOfWork)
            .Include(a => a.Vendor)
            .OrderBy(a => a.IsDeleted)
            .ThenBy(a => a.Vendor!.FullName)
            .ToListAsync(ct);

        if (rows.Count == 0)
            return Result.Success<IReadOnlyList<VendorShiftAllocationDto>>(Array.Empty<VendorShiftAllocationDto>());

        // Compute CurrentlyAssigned per-vendor in one query — grouping
        // by VendorId. Indexed on (event_id, status) so
        // this should hit the existing assignment indexes.
        var vendorIds = rows.Select(r => r.VendorId).Distinct().ToList();
        var occupancy = await _db.EventAssignments
            .Where(AssignmentCapacityRules.OccupiesSeatOnShift(req.ShiftId))
            .Where(a => a.VendorId != null && vendorIds.Contains(a.VendorId.Value))
            .GroupBy(a => a.VendorId!.Value)
            .Select(g => new { VendorId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.VendorId, x => x.Count, ct);

        // Phase D step 18: per-vendor placeholder invite status.
        //   Surfaces "Pending Invite / Accepted / Rejected" in the
        //   admin Vendor Quotas panel so the placeholder rows can be
        //   hidden from the Crew Assignments list without losing the
        //   vendor's response visibility.
        //
        //   We look at placeholder rows (CrewId == null) on THIS shift
        //   for the vendors we care about, take the latest by CreatedAt
        //   per (vendor) so re-invites win over old terminal rows.
        var eventId = rows.First().Shift!.EventId;
        var phRows = await _db.EventAssignments
            .Where(a => a.EventId == eventId
                     && a.ShiftId == req.ShiftId
                     && a.CrewId == null
                     && !a.IsDeleted
                     && a.VendorId != null
                     && vendorIds.Contains(a.VendorId.Value))
            .Select(a => new { VendorId = a.VendorId!.Value, a.Status, a.CreatedAt })
            .ToListAsync(ct);
        var inviteStatus = phRows
            .GroupBy(x => x.VendorId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(x => x.CreatedAt).First().Status.ToString());

        var dtos = rows.Select(a => new VendorShiftAllocationDto(
            a.Id,
            a.ShiftId, a.VendorId,
            a.Vendor?.FullName ?? a.Vendor?.Mobile ?? "(unnamed vendor)",
            a.Shift!.EventId,
            a.Shift.ScopeOfWorkId,
            a.Shift.ScopeOfWork?.Name ?? "(unknown)",
            a.Quota,
            occupancy.GetValueOrDefault(a.VendorId, 0),
            a.IsDeleted, a.CreatedAt, a.UpdatedAt,
            inviteStatus.GetValueOrDefault(a.VendorId))).ToList();

        return Result.Success<IReadOnlyList<VendorShiftAllocationDto>>(dtos);
    }
}
