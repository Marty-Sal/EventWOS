using EventWOS.Application.Events.DTOs;
using EventWOS.Application.Interfaces;
using EventWOS.Application.Events.Shifts;
using EventWOS.Domain.Interfaces;
using EventWOS.Domain.Entities;
using EventWOS.Domain.Enums;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Events.Commands;

/// <summary>
/// Phase D step 1: admin/manager adds a NEW shift to an existing event.
/// Auto-grows the event's MaxCrew by the new shift's crew count so the
/// legacy capacity field stays in sync with SUM(active shifts.CrewCount).
///
/// Refused when the event is Completed/Cancelled — those states are
/// terminal and editing them is already blocked at the page level too.
/// Same scope can appear on multiple shifts (different end-times etc.) —
/// no uniqueness check on ScopeOfWorkId, matching create-time behaviour.
/// </summary>
public sealed record AddEventShiftCommand(
    Guid     EventId,
    Guid     ScopeOfWorkId,
    int      CrewCount,
    DateTime StartAt,
    DateTime? EndAt,
    Guid     ActorUserId
) : IRequest<Result<EventShiftDto>>;

public sealed class AddEventShiftHandler
    : IRequestHandler<AddEventShiftCommand, Result<EventShiftDto>>
{
    private readonly IAppDbContext _db;
    private readonly IUnitOfWork   _uow;

    public AddEventShiftHandler(IAppDbContext db, IUnitOfWork uow)
    {
        _db = db; _uow = uow;
    }

    public async Task<Result<EventShiftDto>> Handle(AddEventShiftCommand req, CancellationToken ct)
    {
        if (req.CrewCount < 1)
            return Result.Failure<EventShiftDto>(new Error("Shift.InvalidCrewCount", "Crew count must be at least 1."));

        var ev = await _db.Events.FirstOrDefaultAsync(e => e.Id == req.EventId, ct);
        if (ev is null)
            return Result.Failure<EventShiftDto>(new Error("Event.NotFound", "Event not found."));
        if (ev.Status == EventStatus.Completed || ev.Status == EventStatus.Cancelled)
            return Result.Failure<EventShiftDto>(new Error("Event.Terminal",
                "Completed or cancelled events cannot be edited."));

        var scope = await _db.ScopesOfWork.FirstOrDefaultAsync(
            s => s.Id == req.ScopeOfWorkId, ct);
        if (scope is null)
            return Result.Failure<EventShiftDto>(new Error("Shift.InvalidScope",
                "Scope of work not found or archived."));

        // Phase D step 2: per-shift start time. Caller defaults to event
        // StartAt when they don't want to vary it; we still bounds-check
        // here in case a stale or malicious client sends junk.
        var boundsCheck = ShiftTimeBounds.Validate(ev, req.StartAt, req.EndAt);
        if (boundsCheck.IsFailure)
            return Result.Failure<EventShiftDto>(boundsCheck.Error);

        EventShift shift;
        try
        {
            shift = new EventShift(
                eventId:        req.EventId,
                scopeOfWorkId:  req.ScopeOfWorkId,
                crewCount:      req.CrewCount,
                startAt:        req.StartAt,
                endAt:          req.EndAt,
                createdByUserId: req.ActorUserId);
        }
        catch (ArgumentException ex)
        {
            return Result.Failure<EventShiftDto>(new Error("Shift.Invalid", ex.Message));
        }
        _db.EventShifts.Add(shift);

        // Auto-grow MaxCrew. SumAsync sees only committed rows — the
        // just-Added shift is tracked but unsaved and will not appear
        // in the server-side SUM. So sum the ALREADY-committed shifts
        // and add the new one's CrewCount ourselves. Same pattern as
        // UpdateEventShiftCommand: keeps the Application layer on
        // IAppDbContext instead of leaning on _db.Entry() (a
        // DbContext-only API).
        var existingTotal = await _db.EventShifts
            .Where(s => s.EventId == req.EventId)
            .SumAsync(s => s.CrewCount, ct);
        var newTotal = existingTotal + req.CrewCount;
        ev.RecomputeCapacityFromShifts(newTotal);

        await _uow.SaveChangesAsync(ct);

        // Brand-new shift: both counts are zero — no assignments exist yet
        // (assigned = OccupiesSeat count, reserved = ReservesSeatOnShift
        // count, and the row has no children).
        return Result.Success(new EventShiftDto(
            shift.Id, shift.EventId, shift.ScopeOfWorkId, scope.Name,
            shift.CrewCount,
            AssignedCrew: 0,
            ReservedCrew: 0,
            shift.StartAt, shift.EndAt));
    }
}
