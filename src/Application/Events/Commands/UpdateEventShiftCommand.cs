using EventWOS.Application.Events.DTOs;
using EventWOS.Application.Interfaces;
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
    private readonly IAppDbContext _db;
    private readonly IUnitOfWork   _uow;

    public UpdateEventShiftHandler(IAppDbContext db, IUnitOfWork uow)
    {
        _db = db; _uow = uow;
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

        // Recompute event MaxCrew. EF tracks the in-memory shift mutation,
        // so SumAsync sees the new value.
        var newTotal = await _db.EventShifts
            .Where(s => s.EventId == shift.EventId)
            .SumAsync(s => s.CrewCount, ct);

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

        await _uow.SaveChangesAsync(ct);

        // Reload scope name if changed (or fetch existing).
        scope ??= await _db.ScopesOfWork.FirstOrDefaultAsync(s => s.Id == shift.ScopeOfWorkId, ct);

        return Result.Success(new EventShiftDto(
            shift.Id, shift.EventId, shift.ScopeOfWorkId, scope?.Name ?? "(unknown)",
            shift.CrewCount,
            AssignedCrew: seatsOnThisShift,
            ReservedCrew: reservedOnThisShift,
            shift.StartAt, shift.EndAt));
    }
}
