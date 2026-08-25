using EventOpsOracle.Application.Interfaces;
using EventOpsOracle.Application.Notifications.Abstractions;
using EventOpsOracle.Application.Notifications.Contracts;
using EventOpsOracle.Application.Events.DTOs;
using EventOpsOracle.Domain.Entities;
using EventOpsOracle.Domain.Enums;
using EventOpsOracle.Domain.Interfaces;
using EventOpsOracle.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;
using EventOpsOracle.Domain.Rules;

namespace EventOpsOracle.Application.Events.Commands;

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
    private readonly INotificationDispatcher _notifications;

    public AssignCrewHandler(
        IAppDbContext db, IUnitOfWork uow, INotificationPusher push, INotificationDispatcher notifications)
    {
        _db            = db;
        _uow           = uow;
        _push          = push;
        _notifications = notifications;
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
            _shiftId = await EventOpsOracle.Application.Events.Shifts.DefaultShiftResolver.ResolveAsync(
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
        // ix_event_assignments_event_crew_shift_unique.
        //
        // Vendor-only requests used to skip this check on the grounds that "multiple
        // placeholders per shift are valid (each anchors a slot)" -- true only while
        // every placeholder WAS a seat. Seats now come from the vendor's quota, so a
        // second anchor buys nothing and the capacity guard no longer refuses it:
        // pressing Assign again on a vendor already working the shift inserted a
        // fresh Invited row, and their My Events flipped back to "AWAITING RESPONSE
        // -- Accept the shift to start adding your crew" on a shift they had already
        // accepted and staffed. One vendor holds ONE invitation per shift.
        // BUGFIX: the old check only looked for ACTIVE rows and then always
        // INSERTed, so re-assigning a crew member who holds a terminal row
        // (Declined / RejectedByVendor / RejectedByManager / NoShow) on this
        // shift produced a second row for the same (event, crew, shift) tuple
        // and tripped the partial unique index
        // ix_event_assignments_event_crew_shift_unique (… WHERE is_deleted =
        // false) → Postgres 23505 / DbUpdateException. We now fetch the row
        // itself and resurrect terminal ones in place, matching
        // VendorAssignCrewHandler.
        EventAssignment? existingRow = null;
        EventAssignment? existingAnchor = null;
        if (req.CrewId.HasValue)
        {
            existingRow = await _db.EventAssignments.FirstOrDefaultAsync(
                a => a.EventId == req.EventId
                  && a.CrewId  == req.CrewId
                  && a.ShiftId == _shiftId.Value, ct);

            if (existingRow is not null)
            {
                var isTerminal = existingRow.Status is
                    AssignmentStatus.Declined          or
                    AssignmentStatus.RejectedByVendor  or
                    AssignmentStatus.RejectedByManager or
                    AssignmentStatus.NoShow;
                if (!isTerminal)
                    return Result.Failure<EventAssignmentDto>(new Error(
                        "Assignment.Duplicate",
                        "Crew is already assigned to this shift."));
            }
        }
        else if (req.VendorId.HasValue)
        {
            existingAnchor = await _db.EventAssignments.FirstOrDefaultAsync(
                a => a.EventId  == req.EventId
                  && a.CrewId   == null
                  && a.VendorId == req.VendorId
                  && a.ShiftId  == _shiftId.Value, ct);

            // RejectedByVendor is the one state worth re-inviting from, and the
            // domain already has the transition for it (ManagerReinviteVendor).
            // Anything else -- Invited, accepted, approved, working -- means the
            // vendor is already on this shift and re-inviting would only reset
            // their own view of it.
            if (existingAnchor is not null
                && existingAnchor.Status != AssignmentStatus.RejectedByVendor)
            {
                var stateNote = existingAnchor.Status == AssignmentStatus.Invited
                    ? "they have already been invited and have not responded yet"
                    : $"their invitation is already {existingAnchor.Status}";

                return Result.Failure<EventAssignmentDto>(new Error(
                    "Assignment.VendorAlreadyOnShift",
                    $"{vendor!.FullName} is already on this shift — {stateNote}. " +
                    "To give them more seats, raise their quota in Vendor Quotas."));
            }
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

        // Committed seats, NOT raw reserved rows. A vendor's placeholder anchor and
        // the quota it stands for are the same seats -- counting both made a
        // capacity-3 shift with one vendor (quota 2, 2 crew placed) report itself
        // full while the Vendor Quotas panel showed a free seat, so the admin was
        // refused a seat that demonstrably existed.
        var shiftRows = await _db.EventAssignments
            .Where(AssignmentCapacityRules.ReservesSeatOnShift(_shiftId.Value))
            .Select(a => new { a.VendorId, IsPlaceholder = a.CrewId == null })
            .ToListAsync(ct);

        var shiftAllocations = await _db.VendorShiftAllocations
            .Where(a => a.ShiftId == _shiftId.Value && !a.IsDeleted)
            .Select(a => new { a.VendorId, a.Quota })
            .ToListAsync(ct);

        var shiftCommitted = AssignmentCapacityRules.CommittedSeatsOnShift(
            shiftAllocations.Select(a => (a.VendorId, a.Quota)),
            shiftRows.Select(r => (r.VendorId, r.IsPlaceholder)));

        // Room for the row about to be added? A vendor with quota headroom is
        // already paid for, so this only bites when the seat is genuinely new.
        var wouldCommit = AssignmentCapacityRules.CommittedSeatsOnShift(
            shiftAllocations.Select(a => (a.VendorId, a.Quota)),
            shiftRows.Select(r => (r.VendorId, r.IsPlaceholder))
                     .Append((req.VendorId, false)));

        if (wouldCommit > shiftEntity.CrewCount)
            return Result.Failure<EventAssignmentDto>(new Error("Assignment.ShiftFull",
                $"Shift is fully reserved ({shiftCommitted}/{shiftEntity.CrewCount} seats). " +
                "Revoke a placeholder or increase shift capacity first."));

        EventAssignment assignment;
        if (existingAnchor is not null)
        {
            // Vendor previously rejected this shift and the manager is asking again:
            // resurrect the same anchor (clears the rejection audit) rather than
            // leaving a rejected row next to a fresh invitation.
            try
            {
                existingAnchor.ManagerReinviteVendor();
            }
            catch (InvalidOperationException ex)
            {
                return Result.Failure<EventAssignmentDto>(new Error("Assignment.CannotReinvite", ex.Message));
            }

            existingAnchor.UpdatedAt = DateTime.UtcNow;
            existingAnchor.UpdatedBy = req.AssignedByUserId;
            assignment = existingAnchor;
        }
        else if (existingRow is not null)
        {
            // Terminal row on this shift → flip back to Invited in place
            // (clears the old response/rejection audit fields) instead of
            // inserting a duplicate that the unique index would reject.
            existingRow.ReInvite(req.VendorId, req.AssignedByUserId);
            existingRow.UpdatedAt = DateTime.UtcNow;
            existingRow.UpdatedBy = req.AssignedByUserId;
            assignment = existingRow;
        }
        else
        {
            assignment = new EventAssignment(req.EventId, req.CrewId, req.VendorId, req.AssignedByUserId);
            assignment.AttachToShift(_shiftId.Value);
            _db.EventAssignments.Add(assignment);
        }
        // One timestamp shared by both notifications below, so a single Assign action
        // produces one coherent pair of keys.
        var invitedAt = DateTime.UtcNow;

        // Staged BEFORE the save so the assignment and its notifications commit in
        // one transaction: if this insert rolls back, nobody is told about an
        // assignment that does not exist, and if the provider is down the
        // assignment still commits with the message waiting in the outbox.
        if (crew is not null)
        {
            _notifications.Enqueue(new NotificationRequest(
                NotificationTemplateCodes.CrewAssignment,
                RecipientUserId: crew.Id,
                // Keyed on the assignment AND the moment of invitation. The assignment
                // id alone is not enough: a terminal row is re-invited by flipping the
                // SAME row back to Invited (ReInvite above), so a crew member who
                // declined and is later invited again would share a key with the first
                // invitation and the platform would drop the second as a duplicate --
                // leaving them never told they are wanted again.
                //
                // Ticks still absorb the case this key was written for: a double-clicked
                // Assign button or a retried request lands inside one tick and collapses.
                BusinessEventKey: $"assignment:{assignment.Id}:invited:{invitedAt.Ticks}",
                Data: new Dictionary<string, string?>
                {
                    [NotificationTokens.RecipientName] = crew.FullName,
                    [NotificationTokens.EventName]     = ev.Title,
                    [NotificationTokens.EventDate]     = ev.StartAt.ToString("dd MMM yyyy"),
                    [NotificationTokens.EventTime]     = ev.StartAt.ToString("HH:mm"),
                    [NotificationTokens.VendorName]    = vendor?.FullName ?? "Manager (direct)"
                },
                EventId: ev.Id,
                ActorUserId: req.AssignedByUserId));
        }
        else if (vendor is not null)
        {
            _notifications.Enqueue(new NotificationRequest(
                NotificationTemplateCodes.VendorEventInvited,
                RecipientUserId: vendor.Id,
                BusinessEventKey: $"assignment:{assignment.Id}:vendor-invited:{invitedAt.Ticks}",
                Data: new Dictionary<string, string?>
                {
                    [NotificationTokens.RecipientName] = vendor.FullName,
                    [NotificationTokens.EventName]     = ev.Title,
                    [NotificationTokens.EventDate]     = ev.StartAt.ToString("dd MMM yyyy"),
                    [NotificationTokens.EventTime]     = ev.StartAt.ToString("HH:mm")
                },
                EventId: ev.Id,
                ActorUserId: req.AssignedByUserId));
        }

        await _uow.SaveChangesAsync(ct);

        // The legacy SignalR push stays for now, and deliberately so: the Blazor
        // client subscribes to these specific event names ("AssignmentInvite",
        // "VendorEventAssigned") to refresh its lists, while the notification
        // platform's in-app sender emits "NotificationReceived", which nothing is
        // listening to yet. Removing this before the client is wired up would
        // trade a working live UI for a silent one. It goes once the client
        // consumes the platform feed.
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
