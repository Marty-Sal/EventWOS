using EventWOS.Application.Events.DTOs;
using EventWOS.Application.Interfaces;
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

        // Shift's StartAt defaults to the event's StartAt; the UI doesn't
        // surface per-shift start today (only end). Keeps the create-time
        // shape consistent.
        var shift = new EventShift(
            eventId:        req.EventId,
            scopeOfWorkId:  req.ScopeOfWorkId,
            crewCount:      req.CrewCount,
            startAt:        ev.StartAt,
            endAt:          req.EndAt,
            createdByUserId: req.ActorUserId);
        _db.EventShifts.Add(shift);

        // Auto-grow MaxCrew. SUM the existing active crew counts + the new
        // shift. Global query filter already excludes archived rows.
        var existingTotal = await _db.EventShifts
            .Where(s => s.EventId == req.EventId)
            .SumAsync(s => s.CrewCount, ct);
        ev.RecomputeCapacityFromShifts(existingTotal + req.CrewCount);

        await _uow.SaveChangesAsync(ct);

        return Result.Success(new EventShiftDto(
            shift.Id, shift.EventId, shift.ScopeOfWorkId, scope.Name,
            shift.CrewCount, 0, shift.StartAt, shift.EndAt));
    }
}
