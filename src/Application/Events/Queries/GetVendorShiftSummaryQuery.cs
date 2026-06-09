using EventWOS.Application.Interfaces;
using EventWOS.Domain.Enums;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Events.Queries;

/// <summary>
/// Phase D step 11: per-shift summary tailored to ONE vendor on ONE event.
/// Surfaces:
///   - the vendor's quota on each shift (0 + Enforced=false when the shift
///     is in legacy "no allocations anywhere" mode - capacity is shared)
///   - how many of THIS vendor's invitations on the shift are in each
///     lifecycle bucket (invited / accepted / approved / final / declined
///     / no-show / placeholders)
///   - free seats this vendor can still invite into right now
///
/// Used by MyEvents.razor's Assign Crew modal to drive:
///   1. the per-shift summary card ("Box Office - 5 invited, 3 approved")
///   2. the "Invite All" button (knows how many seats are left)
///   3. the shift picker dropdown for multi-shift events
///
/// Auth lives at the controller - caller MUST be the vendor whose summary
/// they're asking for (we don't accept a vendorId param; we read it from
/// the auth principal at the controller layer).
/// </summary>
public sealed record GetVendorShiftSummaryQuery(Guid EventId, Guid VendorId)
    : IRequest<Result<IReadOnlyList<VendorShiftSummaryDto>>>;

/// <summary>
/// One row per shift the vendor can see. SeatsFree is the vendor's own
/// headroom - for enforced shifts it's Quota minus placeholders+real seats
/// the vendor currently holds; for legacy (Enforced=false) shifts it's
/// shift.CrewCount minus reserved-on-shift (any vendor).
/// </summary>
public sealed record VendorShiftSummaryDto(
    Guid     ShiftId,
    string   ScopeName,
    DateTime StartAt,
    DateTime? EndAt,
    int      ShiftCapacity,
    int      Quota,
    bool     Enforced,
    int      Placeholders,
    int      Invited,
    int      Accepted,
    int      Approved,
    int      Final,
    int      Declined,
    int      NoShow,
    int      SeatsFree
);

public sealed class GetVendorShiftSummaryHandler
    : IRequestHandler<GetVendorShiftSummaryQuery, Result<IReadOnlyList<VendorShiftSummaryDto>>>
{
    private readonly IAppDbContext _db;
    public GetVendorShiftSummaryHandler(IAppDbContext db) => _db = db;

    public async Task<Result<IReadOnlyList<VendorShiftSummaryDto>>> Handle(
        GetVendorShiftSummaryQuery req, CancellationToken ct)
    {
        var shifts = await _db.EventShifts
            .Where(s => s.EventId == req.EventId)
            .Include(s => s.ScopeOfWork)
            .OrderBy(s => s.StartAt)
            .ToListAsync(ct);
        if (shifts.Count == 0)
            return Result.Success<IReadOnlyList<VendorShiftSummaryDto>>(Array.Empty<VendorShiftSummaryDto>());

        var shiftIds = shifts.Select(s => s.Id).ToList();

        var allocs = await _db.VendorShiftAllocations
            .Where(a => shiftIds.Contains(a.ShiftId) && !a.IsDeleted)
            .Select(a => new { a.ShiftId, a.VendorId, a.Quota })
            .ToListAsync(ct);

        var perShiftTotal = allocs
            .GroupBy(a => a.ShiftId)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Quota));

        var mine = allocs
            .Where(a => a.VendorId == req.VendorId)
            .GroupBy(a => a.ShiftId)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Quota));

        var myRows = await _db.EventAssignments
            .Where(a => a.EventId == req.EventId
                     && a.VendorId == req.VendorId
                     && !a.IsDeleted
                     && a.ShiftId != null
                     && shiftIds.Contains(a.ShiftId.Value))
            .Select(a => new { ShiftId = a.ShiftId!.Value, a.Status, IsPlaceholder = a.CrewId == null })
            .ToListAsync(ct);

        var legacyShiftIds = shifts
            .Where(s => !perShiftTotal.ContainsKey(s.Id) || perShiftTotal[s.Id] == 0)
            .Select(s => s.Id)
            .ToList();

        var legacyReserved = new Dictionary<Guid, int>();
        if (legacyShiftIds.Count > 0)
        {
            var rows = await _db.EventAssignments
                .Where(a => a.ShiftId != null
                         && legacyShiftIds.Contains(a.ShiftId.Value)
                         && !a.IsDeleted
                         && a.Status != AssignmentStatus.Declined
                         && a.Status != AssignmentStatus.RejectedByVendor
                         && a.Status != AssignmentStatus.RejectedByManager
                         && a.Status != AssignmentStatus.NoShow)
                .GroupBy(a => a.ShiftId!.Value)
                .Select(g => new { ShiftId = g.Key, Count = g.Count() })
                .ToListAsync(ct);
            foreach (var r in rows) legacyReserved[r.ShiftId] = r.Count;
        }

        var dtos = new List<VendorShiftSummaryDto>(shifts.Count);
        foreach (var s in shifts)
        {
            var enforced = perShiftTotal.TryGetValue(s.Id, out var tot) && tot > 0;
            var quota    = mine.TryGetValue(s.Id, out var q) ? q : 0;

            var rows = myRows.Where(r => r.ShiftId == s.Id).ToList();
            int placeholders = rows.Count(r => r.IsPlaceholder && r.Status == AssignmentStatus.Invited);
            int invited      = rows.Count(r => !r.IsPlaceholder && r.Status == AssignmentStatus.Invited);
            int accepted     = rows.Count(r => r.Status == AssignmentStatus.VendorAccepted);
            int approved     = rows.Count(r => r.Status == AssignmentStatus.VendorApproved
                                            || r.Status == AssignmentStatus.PendingManagerApproval);
            int final        = rows.Count(r => r.Status == AssignmentStatus.ManagerApproved
                                            || r.Status == AssignmentStatus.Confirmed
                                            || r.Status == AssignmentStatus.Attended);
            int declined     = rows.Count(r => r.Status == AssignmentStatus.Declined
                                            || r.Status == AssignmentStatus.RejectedByVendor
                                            || r.Status == AssignmentStatus.RejectedByManager);
            int noShow       = rows.Count(r => r.Status == AssignmentStatus.NoShow);

            int seatsFree;
            if (enforced)
            {
                var occupies = placeholders + invited + accepted + approved + final;
                seatsFree = Math.Max(0, quota - occupies);
            }
            else
            {
                var reserved = legacyReserved.GetValueOrDefault(s.Id, 0);
                seatsFree = Math.Max(0, s.CrewCount - reserved);
            }

            dtos.Add(new VendorShiftSummaryDto(
                ShiftId:       s.Id,
                ScopeName:     s.ScopeOfWork?.Name ?? "(unknown)",
                StartAt:       s.StartAt,
                EndAt:         s.EndAt,
                ShiftCapacity: s.CrewCount,
                Quota:         quota,
                Enforced:      enforced,
                Placeholders:  placeholders,
                Invited:       invited,
                Accepted:      accepted,
                Approved:      approved,
                Final:         final,
                Declined:      declined,
                NoShow:        noShow,
                SeatsFree:     seatsFree));
        }

        var visible = dtos.Where(d => d.Enforced ? d.Quota > 0 : true).ToList();
        return Result.Success<IReadOnlyList<VendorShiftSummaryDto>>(visible);
    }
}
