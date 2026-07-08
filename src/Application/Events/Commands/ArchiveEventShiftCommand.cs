using EventWOS.Application.Interfaces;
using EventWOS.Domain.Interfaces;
using EventWOS.Domain.Enums;
using EventWOS.Domain.Rules;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Events.Commands;

/// <summary>
/// Phase D step 1: archive (soft-delete) a shift. Blocked while ANY
/// crew still occupies a seat on the shift — the admin must reject /
/// unassign them first, same rule as VendorShiftAllocation archive.
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
    private readonly IAppDbContext _db;
    private readonly IUnitOfWork   _uow;

    public ArchiveEventShiftHandler(IAppDbContext db, IUnitOfWork uow)
    {
        _db = db; _uow = uow;
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
