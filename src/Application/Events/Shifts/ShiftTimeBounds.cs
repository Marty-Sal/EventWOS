using EventWOS.Domain.Entities;
using EventWOS.Shared.Result;
using MediatR;

namespace EventWOS.Application.Events.Shifts;

/// <summary>
/// Phase D step 2: shared bounds-check for shift start/end relative to the
/// parent event.
///
/// Original Phase D rule required shifts to fit strictly inside
/// [event.StartAt, event.EndAt). User feedback (multi-shift screenshots,
/// 2026-06-09) flagged this as wrong: real ops have load-in crew arriving
/// hours before doors open and teardown crew staying after the event ends.
///
/// New policy:
///   - Shift start may be UP TO 24h BEFORE event.StartAt (load-in window).
///   - Shift start must be BEFORE event.EndAt — otherwise the shift is
///     entirely post-event, which makes no sense for a staffing slot.
///   - Shift end may be UP TO 24h AFTER event.EndAt (teardown window).
///   - Shift end must be AFTER shift start (domain entity enforces this
///     strict inequality, so we don't repeat it here).
///
/// The 24h pad is a sanity guard against typos (e.g. picking Jun 9
/// instead of Jun 10) without blocking legitimate setup/teardown crews.
/// </summary>
public static class ShiftTimeBounds
{
    /// <summary>How far before event.StartAt a shift may begin (load-in).</summary>
    public static readonly TimeSpan PreEventPad  = TimeSpan.FromHours(24);

    /// <summary>How far after event.EndAt a shift may end (teardown).</summary>
    public static readonly TimeSpan PostEventPad = TimeSpan.FromHours(24);

    public static Result<Unit> Validate(Event ev, DateTime startAt, DateTime? endAt)
    {
        if (startAt < ev.StartAt - PreEventPad)
            return Result.Failure<Unit>(new Error(
                "Shift.StartTooEarly",
                $"Shift start time can't be more than {PreEventPad.TotalHours:F0}h before the event start time."));

        if (startAt >= ev.EndAt)
            return Result.Failure<Unit>(new Error(
                "Shift.StartAfterEvent",
                "Shift start time must be before the event end time."));

        if (endAt is { } end && end > ev.EndAt + PostEventPad)
            return Result.Failure<Unit>(new Error(
                "Shift.EndTooLate",
                $"Shift end time can't be more than {PostEventPad.TotalHours:F0}h after the event end time."));

        return Result.Success(Unit.Value);
    }
}
