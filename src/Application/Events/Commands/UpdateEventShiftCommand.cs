using EventWOS.Application.Events.DTOs;
using EventWOS.Application.Events.Common;
using EventWOS.Application.Interfaces;
using EventWOS.Application.Notifications.Abstractions;
using EventWOS.Application.Events.Shifts;
using EventWOS.Domain.Interfaces;
using EventWOS.Domain.Enums;
using EventWOS.Domain.Rules;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Events.Commands;

/// <summary>
/// Phase D step 1: edit an existing shift's crew count, scope, and/or end-time.
///
/// Capacity-shrink guard: cannot drop CrewCount below the number of crew
/// who already occupy a seat on THIS shift (matches Event.MaxCrew shrink
/// behaviour). Scope can also be changed; uniqueness is not enforced so
/// two shifts can share a scope.
///
/// Side-effect: recomputes the event's MaxCrew from SUM(active shifts).
/// </summary>
public sealed record UpdateEventShiftCommand(
    Guid     ShiftId,
    Guid     ScopeOfWorkId,
    int      CrewCount,
    DateTime StartAt,
    DateTime? EndAt
) : IRequest<Result<EventShiftDto>>;

public sealed class UpdateEventShiftHandler
    : IRequestHandler<UpdateEventShiftCommand, Result<EventShiftDto>>
{
    private readonly IAppDbContext           _db;
    private readonly IUnitOfWork             _uow;
    private readonly INotificationDispatcher _notifications;

    public UpdateEventShiftHandler(IAppDbContext db, IUnitOfWork uow,
                                   INotificationDispatcher notifications)
    {
        _db            = db;
        _uow           = uow;
        _notifications = notifications;
    }

    public async Task<Result<EventShiftDto>> Handle(UpdateEventShiftCommand req, CancellationToken ct)
    {
        if (req.CrewCount < 1)
            return Result.Failure<EventShiftDto>(new Error("Shift.InvalidCrewCount", "Crew count must be at least 1."));

        var shift = await _db.EventShifts.FirstOrDefaultAsync(s => s.Id == req.ShiftId, ct);
        if (shift is null)
            return Result.Failure<EventShiftDto>(new Error("Shift.NotFound", "Shift not found."));

        var ev = await _db.Events.FirstOrDefaultAsync(e => e.Id == shift.EventId, ct);
        if (ev is null)
            return Result.Failure<EventShiftDto>(new Error("Event.NotFound", "Parent event not found."));
        if (ev.Status == EventStatus.Completed || ev.Status == EventStatus.Cancelled)
            return Result.Failure<EventShiftDto>(new Error("Event.Terminal",
                "Completed or cancelled events cannot be edited."));

        // Scope validation only if changing scope.
        Domain.Entities.ScopeOfWork? scope = null;
        if (shift.ScopeOfWorkId != req.ScopeOfWorkId)
        {
            scope = await _db.ScopesOfWork.FirstOrDefaultAsync(
                s => s.Id == req.ScopeOfWorkId, ct);
            if (scope is null)
                return Result.Failure<EventShiftDto>(new Error("Shift.InvalidScope",
                    "Scope of work not found or archived."));
        }

        // Count seats currently occupied on THIS shift — domain enforces
        // the shrink rule using this value (real crew only; placeholders
        // are ignored because they can be revoked by shrinking the
        // vendor's allocation).
        var seatsOnThisShift = await _db.EventAssignments
            .Where(AssignmentCapacityRules.OccupiesSeatOnShift(shift.Id))
            .CountAsync(ct);

        // Count RESERVED seats — real crew + placeholder anchors — so the
        // returned DTO exposes the same number the assign-crew capacity
        // gate enforces. Without this the modal would go on displaying
        // "N free" using AssignedCrew and disagree with the server.
        var reservedOnThisShift = await _db.EventAssignments
            .Where(AssignmentCapacityRules.ReservesSeatOnShift(shift.Id))
            .CountAsync(ct);

        var boundsCheck = ShiftTimeBounds.Validate(ev, req.StartAt, req.EndAt);
        if (boundsCheck.IsFailure)
            return Result.Failure<EventShiftDto>(boundsCheck.Error);

        // ── Vendor quotas move with the capacity ────────────────────────────
        //
        // Reported: raising a shift from 2 to 3 changed the crew number and nothing
        // else -- the vendor who owned the shift stayed on a quota of 2, their
        // Assign Crew modal still said "your allocation is full (2/2)", and the
        // extra seat existed on the event while being reachable by nobody.
        //
        // The rule, both directions, is about crew who are actually placed:
        //
        //   * GROWING: the creation paths grant a vendor picked on a shift the WHOLE
        //     shift (Quota == CrewCount), so where one vendor still owns the whole
        //     shift their quota follows the capacity up. Shifts split between
        //     several vendors, or a quota an admin deliberately set below capacity,
        //     are arithmetic we must not guess at -- the new seats stay unallocated
        //     for the admin to grant.
        //
        //   * SHRINKING: only seats a vendor has NOT filled may be taken away.
        //     Unused quota is trimmed (largest headroom first) until the shift is no
        //     longer over-committed; a seat with a crew member in it is never taken.
        //     If the shrink can only be satisfied by removing a vendor from the
        //     shift entirely, we refuse and say so -- dropping a vendor is a
        //     decision for the admin, not a side-effect of editing a number.
        var allocations = await _db.VendorShiftAllocations
            .Where(a => a.ShiftId == shift.Id && !a.IsDeleted)
            .ToListAsync(ct);

        // Crew each vendor has actually placed on this shift -- the floor no
        // trim may cross.
        var placedPerVendor = await _db.EventAssignments
            .Where(AssignmentCapacityRules.OccupiesSeatOnShift(shift.Id))
            .Where(a => a.VendorId != null)
            .GroupBy(a => a.VendorId!.Value)
            .Select(g => new { VendorId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        int PlacedBy(Guid vendorId) =>
            placedPerVendor.FirstOrDefault(x => x.VendorId == vendorId)?.Count ?? 0;

        var oldCrewCount = shift.CrewCount;
        // Snapshot for the SHIFT_CHANGED trigger below: what the people standing on
        // this shift were last told.
        var oldStartAt   = shift.StartAt;
        var oldEndAt     = shift.EndAt;
        var oldScopeId   = shift.ScopeOfWorkId;
        var oldQuotas    = allocations.ToDictionary(a => a.VendorId, a => a.Quota);

        if (req.CrewCount > oldCrewCount)
        {
            var soleOwner = allocations.Count == 1 && allocations[0].Quota == oldCrewCount
                ? allocations[0]
                : null;

            if (soleOwner is not null)
            {
                try
                {
                    soleOwner.UpdateQuota(req.CrewCount, PlacedBy(soleOwner.VendorId));
                }
                catch (InvalidOperationException ex)
                {
                    return Result.Failure<EventShiftDto>(new Error("Shift.WouldOrphanVendorCrew", ex.Message));
                }
            }
        }
        else if (req.CrewCount < oldCrewCount && allocations.Count > 0)
        {
            // Seats held by crew no vendor quota accounts for (direct assignments,
            // or a vendor with no allocation) come off the top -- vendor quotas can
            // only share what is left.
            var allocatedVendorIds = allocations.Select(a => a.VendorId).ToHashSet();
            var unallocatedSeats = await _db.EventAssignments
                .Where(AssignmentCapacityRules.OccupiesSeatOnShift(shift.Id))
                .Where(a => a.VendorId == null || !allocatedVendorIds.Contains(a.VendorId.Value))
                .CountAsync(ct);

            var budget = req.CrewCount - unallocatedSeats;

            // Work out the trimmed quotas before touching anything, so a refusal
            // leaves every allocation untouched.
            var planned = allocations
                .Select(a => new { Alloc = a, Floor = Math.Max(1, PlacedBy(a.VendorId)), Quota = a.Quota })
                .Select(x => new { x.Alloc, x.Floor, Quota = x.Quota })
                .ToList();

            var quotas = planned.ToDictionary(x => x.Alloc.Id, x => x.Quota);

            while (quotas.Values.Sum() > budget)
            {
                // Trim the vendor sitting on the most unused seats first: it spreads
                // an even shrink fairly and takes empty seats before tight ones.
                var next = planned
                    .Where(x => quotas[x.Alloc.Id] > x.Floor)
                    .OrderByDescending(x => quotas[x.Alloc.Id] - x.Floor)
                    .FirstOrDefault();

                if (next is null)
                {
                    // Nothing left to give: either crew occupy the seats, or the only
                    // way down is to remove a vendor from the shift.
                    var blocking = planned
                        .Where(x => PlacedBy(x.Alloc.VendorId) > 0)
                        .Sum(x => PlacedBy(x.Alloc.VendorId));

                    if (blocking > 0)
                        return Result.Failure<EventShiftDto>(new Error("Shift.WouldOrphanVendorCrew",
                            $"Vendors have already assigned {blocking} crew to this shift, so it cannot " +
                            $"be reduced to {req.CrewCount}. Remove those crew first, or reduce capacity " +
                            $"to {blocking + unallocatedSeats} at the lowest."));

                    return Result.Failure<EventShiftDto>(new Error("Shift.WouldDropVendor",
                        $"Reducing this shift to {req.CrewCount} would leave no seats for " +
                        $"{planned.Count} vendor(s) allocated to it. Remove a vendor's allocation " +
                        $"from the Vendor Quotas panel first."));
                }

                quotas[next.Alloc.Id] = quotas[next.Alloc.Id] - 1;
            }

            foreach (var x in planned)
            {
                var target = quotas[x.Alloc.Id];
                if (target == x.Alloc.Quota) continue;

                try
                {
                    x.Alloc.UpdateQuota(target, PlacedBy(x.Alloc.VendorId));
                }
                catch (InvalidOperationException ex)
                {
                    return Result.Failure<EventShiftDto>(new Error("Shift.WouldOrphanVendorCrew", ex.Message));
                }
            }
        }

        try
        {
            shift.Update(req.CrewCount, req.StartAt, req.EndAt, seatsOnThisShift);
            if (shift.ScopeOfWorkId != req.ScopeOfWorkId)
                shift.ChangeScope(req.ScopeOfWorkId);
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure<EventShiftDto>(new Error("Shift.WouldOrphanCrew", ex.Message));
        }
        catch (ArgumentException ex)
        {
            return Result.Failure<EventShiftDto>(new Error("Shift.Invalid", ex.Message));
        }

        // Recompute event MaxCrew. Prior version tried SumAsync here
        // with a comment claiming "EF tracks the in-memory shift
        // mutation, so SumAsync sees the new value." That is FALSE —
        // SumAsync translates to server-side SELECT SUM() and does
        // NOT consult the change tracker, so it reads the pre-Update
        // CrewCount for THIS shift. Result: every resize baked a
        // stale total into MaxCrew (KASHISH Pride showed 21 while
        // shifts totalled 22), and repeated resizes progressively
        // drifted MaxCrew away from SUM(shift.CrewCount).
        //
        // Fix (clean-architecture-friendly): sum the OTHER active
        // shifts on the event via SumAsync — those rows are unchanged
        // and DB-accurate — then add the freshly-updated CrewCount
        // for the shift we just mutated. Doesn't require _db.Entry()
        // (which lives on DbContext, not IAppDbContext), so the
        // Application layer stays on the interface.
        var otherShiftsTotal = await _db.EventShifts
            .Where(s => s.EventId == shift.EventId && s.Id != shift.Id)
            .SumAsync(s => s.CrewCount, ct);
        var newTotal = otherShiftsTotal + req.CrewCount;

        // Floor for event MaxCrew is total seats occupied across ALL shifts
        // on the event — same rule as Event.Update.
        var totalSeatsOnEvent = await _db.EventAssignments
            .Where(a => a.EventId == shift.EventId)
            .Where(AssignmentCapacityRules.OccupiesSeat)
            .CountAsync(ct);

        try
        {
            ev.RecomputeCapacityFromShifts(newTotal, totalSeatsOnEvent);
        }
        catch (InvalidOperationException ex)
        {
            // Belt-and-braces — per-shift guard above should have caught it.
            return Result.Failure<EventShiftDto>(new Error("Event.CapacityFloor", ex.Message));
        }

        // ── SHIFT_CHANGED: tell the people standing on this shift ───────────────
        //
        // Another dormant scenario: the template, policy entry and /my-assignments
        // deep link all existed and nothing raised it, so a shift moving to another
        // hour or another scope of work reached nobody at all.
        //
        // Two audiences, because the two changes are not the same news (see
        // ShiftChangeNotification for the reasoning):
        //   * time or scope moved -> every live crew member and vendor on the shift;
        //   * capacity alone      -> only vendors whose quota actually moved.
        var timeOrScopeMoved = shift.StartAt != oldStartAt
                            || shift.EndAt   != oldEndAt
                            || shift.ScopeOfWorkId != oldScopeId;

        var quotaMovedVendorIds = allocations
            .Where(a => !a.IsDeleted)
            .Where(a => !oldQuotas.TryGetValue(a.VendorId, out var was) || was != a.Quota)
            .Select(a => a.VendorId)
            .ToList();

        if (timeOrScopeMoved || quotaMovedVendorIds.Count > 0)
        {
            var scopeName = (scope ?? await _db.ScopesOfWork
                    .FirstOrDefaultAsync(s => s.Id == shift.ScopeOfWorkId, ct))?.Name
                ?? "(unknown)";

            List<Guid> crewIds   = new();
            List<Guid> vendorIds = quotaMovedVendorIds;

            if (timeOrScopeMoved)
            {
                var people = await _db.EventAssignments
                    .Where(a => a.ShiftId == shift.Id)
                    .Select(a => new { a.CrewId, a.VendorId, a.Status })
                    .ToListAsync(ct);

                crewIds = people
                    .Where(x => x.CrewId != null && ShiftChangeNotification.IsLive(x.Status))
                    .Select(x => x.CrewId!.Value)
                    .Distinct()
                    .ToList();

                vendorIds = vendorIds
                    .Concat(people
                        .Where(x => x.VendorId != null && ShiftChangeNotification.IsLive(x.Status))
                        .Select(x => x.VendorId!.Value))
                    // A vendor holding a quota on the shift is accountable for it even
                    // with nobody placed yet, so they hear about a move too.
                    .Concat(allocations.Where(a => !a.IsDeleted).Select(a => a.VendorId))
                    .Distinct()
                    .ToList();
            }

            // Keyed on the shift's new shape, so pressing Save twice is one message
            // while a real second edit is a second message. Minute stamp for the same
            // revert-to-an-earlier-shape reason as EVENT_UPDATED.
            var changeKey = $"{shift.StartAt:yyyyMMddHHmm}-{shift.EndAt:yyyyMMddHHmm}" +
                            $"-{shift.ScopeOfWorkId.ToString()[..8]}-{shift.CrewCount}" +
                            $":{DateTime.UtcNow:yyyyMMddHHmm}";

            var requests = ShiftChangeNotification.Build(
                ev, shift.Id,
                ShiftChangeNotification.Label(scopeName, shift.StartAt, shift.EndAt),
                changeKey, crewIds, vendorIds);

            if (requests.Count > 0)
                _notifications.Enqueue(requests);
        }

        await _uow.SaveChangesAsync(ct);

        // Committed seats for the returned DTO, so the modal's "N free" agrees
        // with the Vendor Quotas panel the moment the resize lands (allocations
        // is already the post-update state).
        var rowsOnThisShift = await _db.EventAssignments
            .Where(AssignmentCapacityRules.ReservesSeatOnShift(shift.Id))
            .Select(a => new { a.VendorId, IsPlaceholder = a.CrewId == null })
            .ToListAsync(ct);

        var committedOnThisShift = AssignmentCapacityRules.CommittedSeatsOnShift(
            allocations.Where(a => !a.IsDeleted).Select(a => (a.VendorId, a.Quota)),
            rowsOnThisShift.Select(r => (r.VendorId, r.IsPlaceholder)));

        // Reload scope name if changed (or fetch existing).
        scope ??= await _db.ScopesOfWork.FirstOrDefaultAsync(s => s.Id == shift.ScopeOfWorkId, ct);

        return Result.Success(new EventShiftDto(
            shift.Id, shift.EventId, shift.ScopeOfWorkId, scope?.Name ?? "(unknown)",
            shift.CrewCount,
            AssignedCrew: seatsOnThisShift,
            ReservedCrew: reservedOnThisShift,
            CommittedCrew: committedOnThisShift,
            shift.StartAt, shift.EndAt));
    }
}
