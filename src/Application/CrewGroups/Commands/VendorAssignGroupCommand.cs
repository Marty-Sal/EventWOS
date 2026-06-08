using EventWOS.Application.CrewGroups.DTOs;
using EventWOS.Application.Interfaces;
using EventWOS.Domain.Entities;
using EventWOS.Domain.Enums;
using EventWOS.Domain.Interfaces;
using EventWOS.Domain.Rules;
using EventWOS.Application.VendorAllocations.Internal;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.CrewGroups.Commands;

/// <summary>
/// Vendor self-service: invite every crew in a group to an event the vendor
/// is on. Reuses the same per-member rules as VendorAssignCrewCommand:
/// vendor must be on the event, must have accepted the manager invite, each
/// crew must be in roster, no duplicate row, capacity respected.
///
/// Returns an aggregate result so the UI can show "Invited 3, Skipped 2 already
/// on event, Failed 1 (capacity reached)" — no single failure short-circuits.
/// </summary>
public sealed record VendorAssignGroupCommand(
    Guid EventId,
    Guid GroupId,
    Guid VendorUserId
) : IRequest<Result<VendorAssignGroupResultDto>>;

public sealed class VendorAssignGroupHandler
    : IRequestHandler<VendorAssignGroupCommand, Result<VendorAssignGroupResultDto>>
{
    private readonly IAppDbContext       _db;
    private readonly IUnitOfWork         _uow;
    private readonly INotificationPusher _push;

    public VendorAssignGroupHandler(IAppDbContext db, IUnitOfWork uow, INotificationPusher push)
    {
        _db = db; _uow = uow; _push = push;
    }

    public async Task<Result<VendorAssignGroupResultDto>> Handle(
        VendorAssignGroupCommand req, CancellationToken ct)
    {
        // ── Pre-checks identical to VendorAssignCrewCommand ───────────────────
        var ev = await _db.Events.FirstOrDefaultAsync(e => e.Id == req.EventId, ct);
        if (ev is null)
            return Result.Failure<VendorAssignGroupResultDto>(new Error("Event.NotFound", "Event not found."));
        if (ev.Status == EventStatus.Completed || ev.Status == EventStatus.Cancelled)
            return Result.Failure<VendorAssignGroupResultDto>(new Error("Event.InvalidStatus", "Event is closed."));

        var grp = await _db.CrewGroups.FirstOrDefaultAsync(g => g.Id == req.GroupId, ct);
        if (grp is null)
            return Result.Failure<VendorAssignGroupResultDto>(new Error("CrewGroup.NotFound", "Group not found."));
        if (grp.VendorId != req.VendorUserId)
            return Result.Failure<VendorAssignGroupResultDto>(new Error("CrewGroup.Forbidden", "That group does not belong to you."));

        var vendorRows = await _db.EventAssignments
            .Where(a => a.EventId == req.EventId && a.VendorId == req.VendorUserId)
            .Select(a => new { a.Status })
            .ToListAsync(ct);

        if (vendorRows.Count == 0)
            return Result.Failure<VendorAssignGroupResultDto>(new Error(
                "Vendor.NotOnEvent",
                "You are not assigned to this event. Ask the event manager to add you first."));

        var hasActive = vendorRows.Any(r =>
            r.Status != AssignmentStatus.Declined
         && r.Status != AssignmentStatus.RejectedByManager
         && r.Status != AssignmentStatus.RejectedByVendor);
        if (!hasActive)
            return Result.Failure<VendorAssignGroupResultDto>(new Error(
                "Vendor.NoActiveAssignment",
                "Your assignment to this event was declined or rejected. Contact the event manager."));

        var hasAcceptedInvite = vendorRows.Any(r =>
            r.Status != AssignmentStatus.Invited
         && r.Status != AssignmentStatus.Declined
         && r.Status != AssignmentStatus.RejectedByManager
         && r.Status != AssignmentStatus.RejectedByVendor);
        if (!hasAcceptedInvite)
            return Result.Failure<VendorAssignGroupResultDto>(new Error(
                "Vendor.InviteNotAccepted",
                "Please accept the Manager's invitation to this event before assigning crew."));

        // ── Load members + their User rows in one shot ────────────────────────
        var members = await (
            from m in _db.CrewGroupMembers
            join u in _db.Users on m.CrewId equals u.Id
            where m.CrewGroupId == grp.Id
               && u.Role == UserRole.Crew
               && !u.IsDeleted
               && u.VendorId == req.VendorUserId    // belt-and-braces: still in roster
            select u
        ).ToListAsync(ct);

        if (members.Count == 0)
        {
            return Result.Success(new VendorAssignGroupResultDto(
                grp.Id, grp.Name, 0, 0, 0,
                Array.Empty<string>(), Array.Empty<string>(),
                Array.Empty<VendorAssignGroupFailureDto>()));
        }

        // Existing assignments for the event so we can detect dupes in O(1).
        var existingForEvent = await _db.EventAssignments
            .Where(a => a.EventId == req.EventId && a.CrewId != null)
            .Select(a => a.CrewId!.Value)
            .ToListAsync(ct);
        var existingSet = existingForEvent.ToHashSet();

        // Capacity once, then we just count up as we invite.
        var currentSeats = await _db.EventAssignments
            .Where(a => a.EventId == req.EventId)
            .Where(AssignmentCapacityRules.OccupiesSeat)
            .CountAsync(ct);

        // Phase B: resolve the event's single shift once, before we start
        // inviting. Multi-shift events get rejected with a clear error until
        // Phase C teaches this handler to accept a per-crew shift map. Doing
        // this BEFORE the loop avoids N round trips for a group of 50 crew.
        bool _ambiguousShift = false;
        var _shiftId = await EventWOS.Application.Events.Shifts.DefaultShiftResolver.ResolveAsync(
            _db, req.EventId, ct, x => _ambiguousShift = x);
        if (_ambiguousShift)
            return Result.Failure<VendorAssignGroupResultDto>(new Error("Assignment.AmbiguousShift",
                "Event has multiple shifts — group assign requires a shift picker (Phase C upgrade required)."));
        if (_shiftId is null)
            return Result.Failure<VendorAssignGroupResultDto>(new Error("Assignment.NoShift",
                "Event has no shifts — cannot assign crew."));

        // Phase C step 3: resolve the vendor's quota ONCE before the loop.
        // We then decrement an in-memory counter as we invite, identical
        // to how currentSeats is tracked. Hard errors (NoAllocation) fail
        // the entire group invite — same shape as the existing pre-checks.
        // QuotaExhausted starts at zero remaining but is NOT a hard error
        // up front (the loop turns it into per-crew "skipped" failures so
        // the UI can show partial success — matches the existing capacity
        // overflow shape).
        var _quota = await VendorQuotaChecker.CheckAsync(_db, _shiftId.Value, req.VendorUserId, ct);
        if (_quota.Status == VendorQuotaCheck.NoAllocation)
            return Result.Failure<VendorAssignGroupResultDto>(new Error(
                "Vendor.NoAllocationOnShift",
                "You don't have an allocation on this shift. " +
                "Ask the event manager to grant you a quota first."));
        // Mutable counter for the loop. NotEnforcedYet → effectively
        // infinite (we use int.MaxValue) so the existing capacity check
        // is still the only gate on legacy events.
        bool _quotaEnforced = _quota.Status != VendorQuotaCheck.NotEnforcedYet;
        int  _quotaRemaining = _quotaEnforced ? Math.Max(0, _quota.Remaining) : int.MaxValue;

        var vendor = await _db.Users.FirstOrDefaultAsync(u => u.Id == req.VendorUserId, ct);

        var invited     = new List<(EventAssignment Row, User Crew)>();
        var skipped     = new List<string>();
        var failures    = new List<VendorAssignGroupFailureDto>();

        foreach (var crew in members.OrderBy(c => c.FullName))
        {
            if (existingSet.Contains(crew.Id))
            {
                skipped.Add(crew.FullName);
                continue;
            }
            if (ev.MaxCrew > 0 && currentSeats >= ev.MaxCrew)
            {
                failures.Add(new VendorAssignGroupFailureDto(
                    crew.Id, crew.FullName, $"Event is fully staffed (max {ev.MaxCrew})."));
                continue;
            }
            if (_quotaEnforced && _quotaRemaining <= 0)
            {
                // Friendly per-crew failure — partial success still wins.
                failures.Add(new VendorAssignGroupFailureDto(
                    crew.Id, crew.FullName,
                    $"Your allocation on this shift is full ({_quota.Quota}/{_quota.Quota})."));
                continue;
            }

            var row = new EventAssignment(req.EventId, crew.Id, req.VendorUserId, req.VendorUserId);
            row.AttachToShift(_shiftId.Value);
            _db.EventAssignments.Add(row);
            invited.Add((row, crew));
            existingSet.Add(crew.Id);
            currentSeats++;
            if (_quotaEnforced) _quotaRemaining--;
        }

        if (invited.Count > 0)
            await _uow.SaveChangesAsync(ct);

        // Fire push notifications post-save so we don't notify on a rolled-back tx.
        foreach (var (row, crew) in invited)
        {
            await _push.PushToUserAsync(crew.Id, "AssignmentInvite", new
            {
                assignmentId = row.Id,
                eventTitle   = ev.Title,
                vendorName   = vendor?.FullName ?? "(vendor)",
                eventStart   = ev.StartAt,
                viaGroup     = grp.Name
            }, ct);
        }

        return Result.Success(new VendorAssignGroupResultDto(
            grp.Id, grp.Name,
            invited.Count, skipped.Count, failures.Count,
            invited.Select(x => x.Crew.FullName).ToList(),
            skipped,
            failures));
    }
}
