using EventOpsOracle.Application.Events.Common;
using EventOpsOracle.Application.Interfaces;
using EventOpsOracle.Application.Notifications.Abstractions;
using EventOpsOracle.Domain.Interfaces;
using EventOpsOracle.Domain.Enums;
using EventOpsOracle.Domain.Rules;
using EventOpsOracle.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventOpsOracle.Application.Events.Commands;

/// <summary>
/// Phase D step 1: archive (soft-delete) a shift. Blocked while ANY
/// crew still occupies a seat on the shift — the admin must reject /
/// unassign them first, same rule as VendorShiftAllocation archive.
///
/// A vendor holding seats they have not staffed is NOT a blocker: an invited
/// vendor with zero crew placed is exactly the shift a manager is allowed to
/// delete. Their quota and their invite go with it (see the cleanup below),
/// because leaving them behind left the vendor holding a seat budget and an
/// invitation for a shift that no longer exists — still listed under their
/// My Events, with nowhere to assign anyone.
///
/// Auto-shrinks the event's MaxCrew by the archived shift's CrewCount
/// after the archive lands. The last-active-shift case is special: we
/// refuse the archive instead of leaving the event with zero capacity
/// (the schema requires at least one shift; see Phase B migration).
/// </summary>
public sealed record ArchiveEventShiftCommand(
    Guid ShiftId,
    Guid ActorUserId
) : IRequest<Result<Unit>>;

public sealed class ArchiveEventShiftHandler
    : IRequestHandler<ArchiveEventShiftCommand, Result<Unit>>
{
    private readonly IAppDbContext           _db;
    private readonly IUnitOfWork             _uow;
    private readonly INotificationDispatcher _notifications;

    public ArchiveEventShiftHandler(IAppDbContext db, IUnitOfWork uow,
                                    INotificationDispatcher notifications)
    {
        _db            = db;
        _uow           = uow;
        _notifications = notifications;
    }

    public async Task<Result<Unit>> Handle(ArchiveEventShiftCommand req, CancellationToken ct)
    {
        var shift = await _db.EventShifts.FirstOrDefaultAsync(s => s.Id == req.ShiftId, ct);
        if (shift is null)
            return Result.Failure<Unit>(new Error("Shift.NotFound", "Shift not found."));

        var ev = await _db.Events.FirstOrDefaultAsync(e => e.Id == shift.EventId, ct);
        if (ev is null)
            return Result.Failure<Unit>(new Error("Event.NotFound", "Parent event not found."));
        if (ev.Status == EventStatus.Completed || ev.Status == EventStatus.Cancelled)
            return Result.Failure<Unit>(new Error("Event.Terminal",
                "Completed or cancelled events cannot be edited."));

        // Last-shift guard: every event must have at least one active shift
        // (Phase B invariant). Refuse rather than leave the event stranded.
        var activeShiftCount = await _db.EventShifts
            .Where(s => s.EventId == shift.EventId)
            .CountAsync(ct);
        if (activeShiftCount <= 1)
            return Result.Failure<Unit>(new Error("Shift.LastActive",
                "Cannot archive the only active shift on this event. Add another shift first, or cancel the event."));

        var seatsOnShift = await _db.EventAssignments
            .Where(AssignmentCapacityRules.OccupiesSeatOnShift(shift.Id))
            .CountAsync(ct);

        try
        {
            shift.Archive(req.ActorUserId, seatsOnShift);
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure<Unit>(new Error("Shift.HasActiveCrew", ex.Message));
        }

        // ── Release what the vendors held on this shift ──────────────────────
        //
        // We only get here with zero crew occupying the shift, so no vendor can
        // have anyone placed and every allocation is pure unused budget: safe to
        // archive, and wrong to keep. Same for the placeholder invite anchors
        // (CrewId == null) — soft-deleted the same way RevokeVendorInviteCommand
        // does it, so the shift stops appearing in the vendor's My Events.
        var allocations = await _db.VendorShiftAllocations
            .Where(a => a.ShiftId == shift.Id && !a.IsDeleted)
            .ToListAsync(ct);

        foreach (var allocation in allocations)
            allocation.Archive(req.ActorUserId, currentSeatsOccupied: 0);

        var placeholders = await _db.EventAssignments
            .Where(a => a.ShiftId == shift.Id && a.CrewId == null && !a.IsDeleted)
            .ToListAsync(ct);

        foreach (var placeholder in placeholders)
        {
            placeholder.IsDeleted = true;
            placeholder.DeletedAt = DateTime.UtcNow;
            placeholder.DeletedBy = req.ActorUserId;
        }

        // ── Tell them it is gone ─────────────────────────────────────────────
        //
        // The vendors here hold budget on a shift that is about to vanish from
        // their My Events (fc8a0a2 made it disappear -- correctly, but silently).
        // A vendor watching for a shift to staff, finding it simply absent, has no
        // way to tell a deletion from a bug, so the deletion says so out loud.
        //
        // SHIFT_CHANGED carries it rather than a new template: the seeded body is
        // fixed in production and the seeder never rewrites existing rows, so the
        // label says "(removed)" instead of inventing a token that would render
        // empty for real users.
        var scopeName = (await _db.ScopesOfWork
                .FirstOrDefaultAsync(s => s.Id == shift.ScopeOfWorkId, ct))?.Name ?? "(unknown)";

        var notifyVendorIds = allocations.Select(a => a.VendorId)
            .Concat(placeholders.Where(a => a.VendorId != null).Select(a => a.VendorId!.Value))
            .Distinct()
            .ToList();

        if (notifyVendorIds.Count > 0)
        {
            var requests = ShiftChangeNotification.Build(
                ev, shift.Id,
                ShiftChangeNotification.Label(scopeName, shift.StartAt, shift.EndAt, removed: true),
                // A shift is archived once, so the plain row id is a stable key --
                // no timestamp needed and a retried request cannot say it twice.
                changeKey: "removed",
                crewIds:   Array.Empty<Guid>(),
                vendorIds: notifyVendorIds,
                actorUserId: req.ActorUserId);

            if (requests.Count > 0)
                _notifications.Enqueue(requests);
        }

        // Recompute event MaxCrew now that this shift is soft-deleted.
        // The IsDeleted flag was flipped in-memory by shift.Archive()
        // but the global query filter still sees the DB value (FALSE)
        // — meaning a plain SumAsync WITHOUT an explicit exclude
        // would still include this shift's CrewCount. Exclude by Id
        // to be independent of the tracker state, same pattern as
        // Update/Add for consistency.
        var newTotal = await _db.EventShifts
            .Where(s => s.EventId == shift.EventId && s.Id != shift.Id)
            .SumAsync(s => s.CrewCount, ct);

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
            return Result.Failure<Unit>(new Error("Event.CapacityFloor", ex.Message));
        }

        await _uow.SaveChangesAsync(ct);
        return Result.Success(Unit.Value);
    }
}
