using EventWOS.Application.Interfaces;
using EventWOS.Application.Events.DTOs;
using EventWOS.Domain.Entities;
using EventWOS.Domain.Enums;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;
using EventWOS.Domain.Rules;
using EventWOS.Application.VendorAllocations.Internal;

namespace EventWOS.Application.Events.Commands;

/// <summary>
/// Vendor self-service: attach one of their own crew to an event the vendor
/// has been assigned to. Used after a Manager creates a 'Vendor-only' direct
/// assignment — the vendor then staffs the event with their roster.
/// </summary>
public sealed record VendorAssignCrewCommand(
    Guid EventId,
    Guid CrewId,
    Guid VendorUserId
) : IRequest<Result<EventAssignmentDto>>;

public sealed class VendorAssignCrewHandler : IRequestHandler<VendorAssignCrewCommand, Result<EventAssignmentDto>>
{
    private readonly IAppDbContext       _db;
    private readonly IUnitOfWork         _uow;
    private readonly INotificationPusher _push;
    public VendorAssignCrewHandler(IAppDbContext db, IUnitOfWork uow, INotificationPusher push)
    {
        _db   = db;
        _uow  = uow;
        _push = push;
    }

    public async Task<Result<EventAssignmentDto>> Handle(VendorAssignCrewCommand req, CancellationToken ct)
    {
        var ev = await _db.Events.FirstOrDefaultAsync(e => e.Id == req.EventId, ct);
        if (ev is null) return Result.Failure<EventAssignmentDto>(new Error("Event.NotFound", "Event not found."));
        if (ev.Status == EventStatus.Completed || ev.Status == EventStatus.Cancelled)
            return Result.Failure<EventAssignmentDto>(new Error("Event.InvalidStatus", "Event is closed."));

        // Vendor must already be assigned to this event with an active relationship.
        // We look at *any* row that has them as VendorId so that once they've
        // attached real crew (and the placeholder is removed), they can keep
        // attaching more without getting locked out.
        var anyVendorRow = await _db.EventAssignments
            .Where(a => a.EventId == req.EventId && a.VendorId == req.VendorUserId)
            .Select(a => new { a.Status })
            .ToListAsync(ct);

        if (anyVendorRow.Count == 0)
            return Result.Failure<EventAssignmentDto>(new Error(
                "Vendor.NotOnEvent",
                "You are not assigned to this event. Ask the event manager to add you first."));

        var hasActive = anyVendorRow.Any(r =>
            r.Status != AssignmentStatus.Declined
         && r.Status != AssignmentStatus.RejectedByManager
         && r.Status != AssignmentStatus.RejectedByVendor);

        if (!hasActive)
            return Result.Failure<EventAssignmentDto>(new Error(
                "Vendor.NoActiveAssignment",
                "Your assignment to this event was declined or rejected. Contact the event manager."));

        // Vendor must have accepted the Manager's invite before staffing crew.
        // The placeholder row (CrewId == null) carries the invitation status.
        // Once the vendor has at least one row that is past the Invited stage
        // (VendorAccepted, or any per-crew row they've already placed), they
        // can keep staffing more crew.
        var hasAcceptedInvite = anyVendorRow.Any(r =>
            r.Status != AssignmentStatus.Invited
         && r.Status != AssignmentStatus.Declined
         && r.Status != AssignmentStatus.RejectedByManager
         && r.Status != AssignmentStatus.RejectedByVendor);

        if (!hasAcceptedInvite)
            return Result.Failure<EventAssignmentDto>(new Error(
                "Vendor.InviteNotAccepted",
                "Please accept the Manager's invitation to this event before assigning crew."));

        // Crew must belong to this vendor
        var crew = await _db.Users.FirstOrDefaultAsync(
            u => u.Id == req.CrewId && u.Role == UserRole.Crew && !u.IsDeleted, ct);
        if (crew is null)
            return Result.Failure<EventAssignmentDto>(new Error("Crew.NotFound", "Crew member not found."));
        if (crew.VendorId != req.VendorUserId)
            return Result.Failure<EventAssignmentDto>(new Error("Crew.NotInRoster", "That crew member is not in your roster."));

        // Look for any existing row for this (event, crew) pair. We treat
        // terminal-rejected rows as "spent" — they get resurrected with a
        // fresh Invited status so the vendor's "Re-invite" button actually
        // sends a new invite instead of being blocked by the dead row.
        var existing = await _db.EventAssignments
            .FirstOrDefaultAsync(a => a.EventId == req.EventId && a.CrewId == req.CrewId, ct);

        bool isResurrection = false;
        if (existing is not null)
        {
            // Active rows still block — can't double-invite someone who's
            // already pending or working the event.
            var isTerminal = existing.Status is
                AssignmentStatus.Declined         or
                AssignmentStatus.RejectedByVendor or
                AssignmentStatus.RejectedByManager or
                AssignmentStatus.NoShow;
            if (!isTerminal)
                return Result.Failure<EventAssignmentDto>(new Error(
                    "Assignment.Duplicate", "That crew is already on this event."));

            isResurrection = true;
        }

        // Capacity check — uses centralised rule (excludes declined,
        // rejected, no-show, placeholders, soft-deleted).
        if (ev.MaxCrew > 0)
        {
            var current = await _db.EventAssignments
                .Where(a => a.EventId == req.EventId)
                .Where(AssignmentCapacityRules.OccupiesSeat)
                .CountAsync(ct);
            if (current >= ev.MaxCrew)
                return Result.Failure<EventAssignmentDto>(new Error("Assignment.MaxReached", $"Event is fully staffed (max {ev.MaxCrew})."));
        }

        var vendor = await _db.Users.FirstOrDefaultAsync(u => u.Id == req.VendorUserId, ct);

        // Create the crew assignment, OR resurrect the previously-terminated
        // row (Declined / RejectedByVendor / RejectedByManager / NoShow) by
        // flipping status back to Invited. Resurrection keeps one row per
        // (event, crew) pair so the badge logic on the picker stays simple.
        EventAssignment assignment;
        if (isResurrection && existing is not null)
        {
            existing.VendorReInvite(req.VendorUserId);
            existing.UpdatedAt = DateTime.UtcNow;
            existing.UpdatedBy = req.VendorUserId;
            assignment = existing;
        }
        else
        {
                    // Phase B: every assignment must reference a shift. Until the
        // multi-shift UI lands (Phase C) we auto-resolve to the event's
        // single active shift via DefaultShiftResolver. Ambiguous events
        // (somehow >1 shift) surface a clear error rather than picking
        // one at random.
        bool _ambiguous = false;
        var _shiftId = await EventWOS.Application.Events.Shifts.DefaultShiftResolver.ResolveAsync(
            _db, req.EventId, ct, x => _ambiguous = x);
        if (_ambiguous)
            return Result.Failure<EventAssignmentDto>(new Error("Assignment.AmbiguousShift",
                "Event has multiple shifts — caller must specify which one. (Phase C upgrade required.)"));
        if (_shiftId is null)
            return Result.Failure<EventAssignmentDto>(new Error("Assignment.NoShift",
                "Event has no shifts — cannot assign crew."));

        // Phase C step 3: vendor quota gate. Only fires for fresh-row
        // path (resurrections skip — they're refilling an already-counted
        // seat, blocking them would be a UX trap). Returns NotEnforcedYet
        // on shifts with zero allocations so legacy events still work.
        var _quota = await VendorQuotaChecker.CheckAsync(_db, _shiftId.Value, req.VendorUserId, ct);
        switch (_quota.Status)
        {
            case VendorQuotaCheck.NoAllocation:
                return Result.Failure<EventAssignmentDto>(new Error(
                    "Vendor.NoAllocationOnShift",
                    "You don't have an allocation on this shift. " +
                    "Ask the event manager to grant you a quota first."));
            case VendorQuotaCheck.QuotaExhausted:
                return Result.Failure<EventAssignmentDto>(new Error(
                    "Vendor.QuotaExhausted",
                    $"Your allocation on this shift is full " +
                    $"({_quota.CurrentlyAssigned}/{_quota.Quota}). " +
                    "Ask the event manager to raise your quota, or remove a previously-invited crew."));
            // Allowed and NotEnforcedYet both fall through to the assignment.
        }

        assignment = new EventAssignment(req.EventId, req.CrewId, req.VendorUserId, req.VendorUserId);
        assignment.AttachToShift(_shiftId.Value);
            _db.EventAssignments.Add(assignment);
        }

        // NOTE: We intentionally DO NOT delete the vendor-only placeholder row.
        // The placeholder is the anchor that says "this vendor is assigned to
        // this event" — independent of whether any specific crew got
        // rejected, declined, or removed. Removing it caused events to vanish
        // from the vendor's My Events when all their staffed crew were
        // rejected. CrewId == null is correctly excluded everywhere it
        // matters (capacity count, attendance, payments, rating).

        await _uow.SaveChangesAsync(ct);

        await _push.PushToUserAsync(crew.Id, "AssignmentInvite", new
        {
            assignmentId = assignment.Id,
            eventTitle   = ev.Title,
            vendorName   = vendor?.FullName ?? "(vendor)",
            eventStart   = ev.StartAt
        }, ct);

        return Result.Success(new EventAssignmentDto(
            assignment.Id, ev.Id, ev.Title, ev.Status.ToString(),
            crew.Id, crew.FullName, crew.Mobile,
            crew.DisciplineScore, crew.EventsAttended,
            crew.CrewRating, crew.CrewRatingCount,
            vendor?.Id, vendor?.FullName,
            assignment.Status.ToString(),
            assignment.RejectionReason,
            assignment.CrewRespondedAt,
            assignment.VendorReviewedAt,
            assignment.ManagerReviewedAt,
            assignment.ConfirmedAt, assignment.DeclinedAt,
            assignment.CreatedAt,
            assignment.VendorRating, assignment.RatedAt, assignment.AttendanceNote));
    }
}
