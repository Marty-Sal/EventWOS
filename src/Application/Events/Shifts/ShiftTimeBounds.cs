using EventWOS.Domain.Entities;
using EventWOS.Shared.Result;
using MediatR;

namespace EventWOS.Application.Events.Shifts;

/// <summary>
/// Phase D step 2: shared bounds-check for shift start/end relative to the
/// parent event. A shift must fit inside [event.StartAt, event.EndAt) — you
/// can't open a staffing slot before the event begins or after it ends.
/// EndAt strict-after-StartAt is enforced by the domain entity itself, so
/// we don't repeat it here.
///
/// Returns a Result so callers can surface the failure code without
/// throwing — keeps shift-creation handlers consistent with the rest of
/// the Result-pattern handler shape.
/// </summary>
public static class ShiftTimeBounds
{
    public static Result<Unit> Validate(Event ev, DateTime startAt, DateTime? endAt)
    {
        if (startAt < ev.StartAt)
            return Result.Failure<Unit>(new Error(
                "Shift.StartBeforeEvent",
                "Shift start time can't be before the event start time."));

        if (startAt >= ev.EndAt)
            return Result.Failure<Unit>(new Error(
                "Shift.StartAfterEvent",
                "Shift start time must be before the event end time."));

        if (endAt is { } end && end > ev.EndAt)
            return Result.Failure<Unit>(new Error(
                "Shift.EndAfterEvent",
                "Shift end time can't be after the event end time."));

        return Result.Success(Unit.Value);
    }
}
