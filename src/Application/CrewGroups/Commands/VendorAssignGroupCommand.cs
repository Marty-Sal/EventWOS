using EventWOS.Application.CrewGroups.DTOs;
using EventWOS.Application.Common;
using EventWOS.Application.Interfaces;
using EventWOS.Application.Notifications.Abstractions;
using EventWOS.Application.Notifications.Contracts;
using EventWOS.Domain.Entities;
using EventWOS.Domain.Enums;
using EventWOS.Domain.Interfaces;
using EventWOS.Domain.Rules;
using EventWOS.Application.VendorAllocations.Internal;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

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
    Guid  EventId,
    Guid  GroupId,
    Guid  VendorUserId,
    Guid? ShiftId = null    // Phase C step 6: explicit shift picker. Null = auto-resolve (legacy).
) : IRequest<Result<VendorAssignGroupResultDto>>;

public sealed class VendorAssignGroupHandler
    : IRequestHandler<VendorAssignGroupCommand, Result<VendorAssignGroupResultDto>>
{
    private readonly IAppDbContext       _db;
    private readonly IUnitOfWork         _uow;
    private readonly INotificationPusher _push;
    private readonly INotificationDispatcher _notifications;
    private readonly AppUrlOptions _appUrls;

    public VendorAssignGroupHandler(
        IAppDbContext db, IUnitOfWork uow, INotificationPusher push,
        INotificationDispatcher notifications, IOptions<AppUrlOptions> appUrls)
    {
        _db = db; _uow = uow; _push = push;
        _notifications = notifications; _appUrls = appUrls.Value;
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
               // A saved group can go stale -- a member suspended or rejected
               // after being added must not get silently invited by "Invite All".
               && u.Status == UserStatus.Active
            select u
        ).ToListAsync(ct);

        if (members.Count == 0)
        {
            return Result.Success(new VendorAssignGroupResultDto(
                grp.Id, grp.Name, 0, 0, 0,
                Array.Empty<string>(), Array.Empty<string>(),
                Array.Empty<VendorAssignGroupFailureDto>()));
        }

        // Capacity once, then we just count up as we invite.
        var currentSeats = await _db.EventAssignments
            .Where(a => a.EventId == req.EventId)
            .Where(AssignmentCapacityRules.OccupiesSeat)
            .CountAsync(ct);

        // Phase B: resolve the event's single shift once, before we start
        // inviting. Multi-shift events get rejected with a clear error until
        // Phase C teaches this handler to accept a per-crew shift map. Doing
        // this BEFORE the loop avoids N round trips for a group of 50 crew.
        // Phase C step 6: honour an explicit ShiftId from the vendor portal's
        // shift picker; otherwise auto-resolve as before. Validation is the
        // same single-row lookup we do for individual assignment.
        Guid? _shiftId;
        if (req.ShiftId is { } explicitShift)
        {
            var belongs = await _db.EventShifts.AnyAsync(
                x => x.Id == explicitShift && x.EventId == req.EventId, ct);
            if (!belongs)
                return Result.Failure<VendorAssignGroupResultDto>(new Error("Assignment.ShiftNotOnEvent",
                    "The picked shift does not belong to this event."));
            _shiftId = explicitShift;
        }
        else
        {
            bool _ambiguousShift = false;
            _shiftId = await EventWOS.Application.Events.Shifts.DefaultShiftResolver.ResolveAsync(
                _db, req.EventId, ct, x => _ambiguousShift = x);
            if (_ambiguousShift)
                return Result.Failure<VendorAssignGroupResultDto>(new Error("Assignment.AmbiguousShift",
                    "Event has multiple shifts — pick one before inviting the group."));
            if (_shiftId is null)
                return Result.Failure<VendorAssignGroupResultDto>(new Error("Assignment.NoShift",
                    "Event has no shifts — cannot assign crew."));
        }

        // Phase D step 19: dup detection is per-shift now. The same crew
        // member CAN be invited to a different shift of the same event;
        // the only collision is "already on THIS shift".
        //
        // BUGFIX: this used to project only the ACTIVE crew ids, so a crew
        // member holding a terminal row (Declined / RejectedByVendor /
        // RejectedByManager / NoShow) on this shift looked brand new and we
        // INSERTed a second row for the same (event, crew, shift) tuple —
        // which collides with the partial unique index
        // ix_event_assignments_event_crew_shift_unique (… WHERE is_deleted =
        // false) and blew up the whole batch with Postgres 23505 /
        // DbUpdateException. The old comment claimed "the vendor-assign
        // command will resurrect them", but that resurrection logic lives in
        // VendorAssignCrewHandler and was never reachable from here — which
        // is exactly why re-inviting a manager-rejected crew worked
        // individually but failed via group.
        //
        // We now load the FULL tracked rows (terminal ones included) so the
        // loop below can resurrect in place via VendorReInvite() instead of
        // inserting, mirroring the individual path exactly.
        var shiftRows = await _db.EventAssignments
            .Where(a => a.EventId == req.EventId
                     && a.ShiftId == _shiftId.Value
                     && a.CrewId != null)
            .ToListAsync(ct);
        var rowByCrew = shiftRows
            .GroupBy(a => a.CrewId!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        static bool IsTerminal(EventAssignment a) => a.Status is
            AssignmentStatus.Declined          or
            AssignmentStatus.RejectedByVendor  or
            AssignmentStatus.RejectedByManager or
            AssignmentStatus.NoShow;

        // Guards against a crew appearing twice in one group's member list.
        var processed = new HashSet<Guid>();

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
            if (!processed.Add(crew.Id)) continue;

            // Existing row on THIS shift: active → genuine duplicate (skip),
            // terminal → resurrect it rather than inserting a colliding row.
            rowByCrew.TryGetValue(crew.Id, out var existingRow);
            var isResurrection = false;
            if (existingRow is not null)
            {
                if (!IsTerminal(existingRow))
                {
                    skipped.Add(crew.FullName);
                    continue;
                }
                isResurrection = true;
            }

            // Capacity applies to resurrections too — a re-invited row flips
            // back to Invited and therefore occupies a seat again. Matches
            // VendorAssignCrewHandler, which also capacity-checks both paths.
            if (ev.MaxCrew > 0 && currentSeats >= ev.MaxCrew)
            {
                failures.Add(new VendorAssignGroupFailureDto(
                    crew.Id, crew.FullName, $"Event is fully staffed (max {ev.MaxCrew})."));
                continue;
            }

            // Quota gate deliberately SKIPS resurrections, mirroring
            // VendorAssignCrewHandler's `if (!isResurrection)` branch: a
            // re-invite refills an already-counted seat, so charging it
            // against remaining quota would falsely block re-inviting a
            // rejected crew member with "allocation full".
            if (!isResurrection && _quotaEnforced && _quotaRemaining <= 0)
            {
                // Friendly per-crew failure — partial success still wins.
                failures.Add(new VendorAssignGroupFailureDto(
                    crew.Id, crew.FullName,
                    $"Your allocation on this shift is full ({_quota.Quota}/{_quota.Quota})."));
                continue;
            }

            EventAssignment row;
            if (isResurrection && existingRow is not null)
            {
                // Flips status back to Invited and clears the previous
                // response/rejection audit fields. No INSERT → no unique
                // index collision.
                existingRow.VendorReInvite(req.VendorUserId);
                existingRow.UpdatedAt = DateTime.UtcNow;
                existingRow.UpdatedBy = req.VendorUserId;
                row = existingRow;
            }
            else
            {
                row = new EventAssignment(req.EventId, crew.Id, req.VendorUserId, req.VendorUserId);
                row.AttachToShift(_shiftId.Value);
                _db.EventAssignments.Add(row);
                rowByCrew[crew.Id] = row;
            }

            invited.Add((row, crew));
            currentSeats++;
            if (!isResurrection && _quotaEnforced) _quotaRemaining--;
        }

        // Residual-safety net: with the resurrection branch above a 23505 is
        // no longer expected, but a raw DbUpdateException must never reach the
        // vendor's Assign Crew modal as wall-of-text SQL. Anything that still
        // fails is reported as per-crew failures so the UI shows its normal
        // invited/skipped/failed summary instead of an exception dump. The
        // whole batch shares one SaveChanges, so a throw means nothing was
        // committed — every attempted row moves to `failures`.
        if (invited.Count > 0)
        {
            // One durable invitation per crew member who actually got a row, staged
            // inside the same try as the save. If SaveChanges throws, these outbox rows
            // are on the same DbContext and roll back with everything else -- so a batch
            // that failed to commit cannot leave messages telling people they are booked.
            //
            // Only `invited` is notified: the crew skipped as duplicates already hold a
            // live invitation, and the ones in `failures` never got a row at all.
            var invitedAt = DateTime.UtcNow;
            var link = _appUrls.BaseUrl.TrimEnd('/') + "/my-assignments";

            _notifications.Enqueue(invited.Select(x => new NotificationRequest(
                NotificationTemplateCodes.CrewInvitation,
                RecipientUserId: x.Crew.Id,
                // Per-row and timestamped: group invites resurrect terminal rows in place
                // (VendorReInvite above), so re-inviting a group whose members previously
                // declined must not have every message swallowed as a duplicate.
                BusinessEventKey: $"assignment:{x.Row.Id}:crew-invited:{invitedAt.Ticks}",
                Data: new Dictionary<string, string?>
                {
                    [NotificationTokens.RecipientName] = x.Crew.FullName,
                    [NotificationTokens.VendorName]    = vendor?.FullName ?? "Your vendor",
                    [NotificationTokens.EventName]     = ev.Title,
                    [NotificationTokens.EventDate]     = ev.StartAt.ToString("dd MMM yyyy"),
                    [NotificationTokens.EventTime]     = ev.StartAt.ToString("HH:mm"),
                    [NotificationTokens.VenueName]     = ev.Venue,
                    [NotificationTokens.Link]          = link
                },
                ActorUserId: req.VendorUserId)));

            try
            {
                await _uow.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                foreach (var (_, crew) in invited)
                    failures.Add(new VendorAssignGroupFailureDto(
                        crew.Id, crew.FullName,
                        "Couldn't save this invite — the crew may already have a record on " +
                        "this shift. Try inviting them individually."));

                return Result.Success(new VendorAssignGroupResultDto(
                    grp.Id, grp.Name,
                    0, skipped.Count, failures.Count,
                    Array.Empty<string>(), skipped, failures));
            }
        }

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
