using EventWOS.Application.Interfaces;
using EventWOS.Application.Events.DTOs;
using EventWOS.Domain.Entities;
using EventWOS.Domain.Enums;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;
using EventWOS.Domain.Rules;

namespace EventWOS.Application.Events.Commands;

public sealed record AssignCrewCommand(
    Guid EventId,
    Guid? CrewId,
    Guid? VendorId,
    Guid AssignedByUserId,
    Guid? ShiftId = null
) : IRequest<Result<EventAssignmentDto>>;

public sealed class AssignCrewHandler : IRequestHandler<AssignCrewCommand, Result<EventAssignmentDto>>
{
    private readonly IAppDbContext       _db;
    private readonly IUnitOfWork         _uow;
    private readonly INotificationPusher _push;
    public AssignCrewHandler(IAppDbContext db, IUnitOfWork uow, INotificationPusher push)
    {
        _db   = db;
        _uow  = uow;
        _push = push;
    }

    public async Task<Result<EventAssignmentDto>> Handle(AssignCrewCommand req, CancellationToken ct)
    {
        // Validate at least one of crew/vendor is set
        if (req.CrewId is null && req.VendorId is null)
            return Result.Failure<EventAssignmentDto>(new Error("Assignment.Empty", "Provide a vendor, a crew member, or both."));

        var ev = await _db.Events.FirstOrDefaultAsync(e => e.Id == req.EventId, ct);
        if (ev is null) return Result.Failure<EventAssignmentDto>(new Error("Event.NotFound", "Event not found."));
        if (ev.Status == EventStatus.Completed || ev.Status == EventStatus.Cancelled)
            return Result.Failure<EventAssignmentDto>(new Error("Event.InvalidStatus", "Cannot assign crew to completed/cancelled events."));

        User? crew = null;
        if (req.CrewId.HasValue)
        {
            crew = await _db.Users.FirstOrDefaultAsync(u => u.Id == req.CrewId.Value && u.Role == UserRole.Crew, ct);
            if (crew is null) return Result.Failure<EventAssignmentDto>(new Error("Crew.NotFound", "Crew member not found."));
        }

        User? vendor = null;
        if (req.VendorId.HasValue)
        {
            vendor = await _db.Users.FirstOrDefaultAsync(u => u.Id == req.VendorId.Value && u.Role == UserRole.Vendor, ct);
            if (vendor is null) return Result.Failure<EventAssignmentDto>(new Error("Vendor.NotFound", "Vendor not found."));
        }

        // Phase D step 19: duplicate check is now per-shift (see further
        // below, after shift resolution). The same crew member may be
        // assigned to multiple shifts of one event.

        // Check max crew — only count rows that genuinely occupy a seat
        // (real crew, not declined/rejected/no-show, not placeholder).
        if (ev.MaxCrew > 0)
        {
            var current = await _db.EventAssignments
                .Where(a => a.EventId == req.EventId)
                .Where(AssignmentCapacityRules.OccupiesSeat)
                .CountAsync(ct);
            if (current >= ev.MaxCrew)
                return Result.Failure<EventAssignmentDto>(new Error("Assignment.MaxReached", $"Event is fully staffed (max {ev.MaxCrew})."));
        }

        // Phase D step 3: caller may now specify ShiftId explicitly. We
        // validate it belongs to this event + isn't archived; otherwise
        // fall back to DefaultShiftResolver for single-shift events. For
        // events with >1 shift and no ShiftId we still error out — the
        // admin UI now surfaces a picker, so this only bites bad clients.
        Guid? _shiftId;
        if (req.ShiftId is { } explicitShift)
        {
            var ok = await _db.EventShifts
                .AnyAsync(s => s.Id == explicitShift && s.EventId == req.EventId && !s.IsDeleted, ct);
            if (!ok)
                return Result.Failure<EventAssignmentDto>(new Error("Assignment.InvalidShift",
                    "Selected shift doesn't belong to this event or is archived."));
            _shiftId = explicitShift;
        }
        else
        {
            bool _ambiguous = false;
            _shiftId = await EventWOS.Application.Events.Shifts.DefaultShiftResolver.ResolveAsync(
                _db, req.EventId, ct, x => _ambiguous = x);
            if (_ambiguous)
                return Result.Failure<EventAssignmentDto>(new Error("Assignment.AmbiguousShift",
                    "Event has multiple shifts — pick one in the assignment dialog."));
            if (_shiftId is null)
                return Result.Failure<EventAssignmentDto>(new Error("Assignment.NoShift",
                    "Event has no shifts — cannot assign crew."));
        }

        // Phase D step 19: per-shift duplicate check. A crew member is
        // allowed to work multiple shifts of the same event, but cannot
        // hold two active rows on the same shift. Mirrors the index
        // ix_event_assignments_event_crew_shift_unique. Placeholder
        // requests (CrewId == null) skip this branch — multiple
        // placeholders per shift are valid (each anchors a slot).
        if (req.CrewId.HasValue)
        {
            var dupExists = await _db.EventAssignments.AnyAsync(
                a => a.EventId == req.EventId
                  && a.CrewId  == req.CrewId
                  && a.ShiftId == _shiftId.Value
                  && a.Status != AssignmentStatus.Declined
                  && a.Status != AssignmentStatus.RejectedByVendor
                  && a.Status != AssignmentStatus.RejectedByManager
                  && a.Status != AssignmentStatus.NoShow, ct);
            if (dupExists)
                return Result.Failure<EventAssignmentDto>(new Error(
                    "Assignment.Duplicate",
                    "Crew is already assigned to this shift."));
        }

        // Phase D step 9: enforce per-shift capacity using TOTAL reserved
        // seats (real crew + placeholders), not just OccupiesSeat. The old
        // code only checked event.MaxCrew, which let admins stack
        // placeholders on a shift past its CrewCount as long as no real
        // crew had been added yet. Bug surfaced when KASHISH Pride's Box
        // Office shift (capacity 5) ended up with 6 placeholders under
        // one vendor.
        var shiftEntity = await _db.EventShifts
            .FirstOrDefaultAsync(s => s.Id == _shiftId.Value, ct);
        if (shiftEntity is null)
            return Result.Failure<EventAssignmentDto>(new Error("Assignment.InvalidShift",
                "Selected shift no longer exists."));

        var shiftReserved = await _db.EventAssignments
            .Where(AssignmentCapacityRules.ReservesSeatOnShift(_shiftId.Value))
            .CountAsync(ct);
        if (shiftReserved >= shiftEntity.CrewCount)
            return Result.Failure<EventAssignmentDto>(new Error("Assignment.ShiftFull",
                $"Shift is fully reserved ({shiftReserved}/{shiftEntity.CrewCount} seats). " +
                "Revoke a placeholder or increase shift capacity first."));

        var assignment = new EventAssignment(req.EventId, req.CrewId, req.VendorId, req.AssignedByUserId);
        assignment.AttachToShift(_shiftId.Value);
        _db.EventAssignments.Add(assignment);
        await _uow.SaveChangesAsync(ct);

        // Push notifications
        if (crew is not null)
        {
            // Crew gets invited
            await _push.PushToUserAsync(crew.Id, "AssignmentInvite", new
            {
                assignmentId = assignment.Id,
                eventTitle   = ev.Title,
                vendorName   = vendor?.FullName ?? "Manager (direct)",
                eventStart   = ev.StartAt
            }, ct);
        }
        else if (vendor is not null)
        {
            // Vendor-only: notify vendor that they need to staff this event
            await _push.PushToUserAsync(vendor.Id, "VendorEventAssigned", new
            {
                assignmentId = assignment.Id,
                eventTitle   = ev.Title,
                eventStart   = ev.StartAt
            }, ct);
        }

        // Phase D step 5: surface shift + scope name on the returned DTO so the
        // admin UI can group rows by shift without a re-fetch.
        var shiftInfo = await _db.EventShifts
            .Where(s => s.Id == assignment.ShiftId)
            .Select(s => new { Name = (string?)s.ScopeOfWork.Name, StartAt = (DateTime?)s.StartAt, EndAt = s.EndAt })
            .FirstOrDefaultAsync(ct);
        var shiftScopeName = shiftInfo?.Name;
        var shiftStartAt   = shiftInfo?.StartAt;
        var shiftEndAt     = shiftInfo?.EndAt;

        return Result.Success(new EventAssignmentDto(
            assignment.Id, ev.Id, ev.Title, ev.Status.ToString(),
            crew?.Id ?? Guid.Empty,
            crew?.FullName ?? "(vendor to fill)",
            crew?.Mobile   ?? "",
            crew?.DisciplineScore ?? 0,
            crew?.EventsAttended  ?? 0,
            crew?.CrewRating,
            crew?.CrewRatingCount ?? 0,
            vendor?.Id, vendor?.FullName,
            assignment.Status.ToString(),
            assignment.RejectionReason,
            assignment.CrewRespondedAt,
            assignment.VendorReviewedAt,
            assignment.ManagerReviewedAt,
            assignment.ConfirmedAt, assignment.DeclinedAt,
            assignment.CreatedAt,
            assignment.VendorRating, assignment.RatedAt, assignment.AttendanceNote,
            assignment.ShiftId, shiftScopeName, shiftStartAt, shiftEndAt));
    }
}
