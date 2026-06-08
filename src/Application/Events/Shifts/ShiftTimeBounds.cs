using EventWOS.Domain.Entities;
using EventWOS.Shared.Result;
using MediatR;

namespace EventWOS.Application.Events.Shifts;

/// <summary>
/// Phase D step 2: shared bounds-check for shift start/end relative to the
/// parent event.
///
/// Original Phase D rule required shifts to fit strictly inside
/// [event.StartAt, event.EndAt). User feedback (2026-06-09) flagged this
/// as wrong: real ops have load-in crew arriving early, teardown crew
/// staying late, and some shifts (box office) closing before the event
/// even starts proper. The policy now treats those as legitimate slots.
///
/// New policy:
///   - Shift START may be UP TO 48h BEFORE event.StartAt (load-in window).
///   - Shift START must be BEFORE event.EndAt — otherwise the shift is
///     entirely post-event, which makes no sense for a staffing slot.
///   - Shift END may be UP TO 48h AFTER event.EndAt (teardown window).
///   - Shift END may be BEFORE event.StartAt — e.g. a box office shift
///     that closes once guests are inside. No lower bound enforced here
///     other than: end must be strictly AFTER start (zero/negative-
///     duration shifts are nonsense; the domain entity also guards this
///     as defense-in-depth).
///
/// The 48h pad is a sanity guard against typos (e.g. picking Jun 9
/// instead of Jun 10) without blocking legitimate setup/teardown crews.
/// </summary>
public static class ShiftTimeBounds
{
    /// <summary>How far before event.StartAt a shift may begin (load-in).</summary>
    public static readonly TimeSpan PreEventPad  = TimeSpan.FromHours(48);

    /// <summary>How far after event.EndAt a shift may end (teardown).</summary>
    public static readonly TimeSpan PostEventPad = TimeSpan.FromHours(48);

    public static Result<Unit> Validate(Event ev, DateTime startAt, DateTime? endAt)
    {
        // Lower bound on start: catches "wrong calendar day" typos.
        if (startAt < ev.StartAt - PreEventPad)
            return Result.Failure<Unit>(new Error(
                "Shift.StartTooEarly",
                $"Shift start time can't be more than {PreEventPad.TotalHours:F0}h before the event start time."));

        // Upper bound on start: a shift starting at-or-after the event
        // ends is a no-op staffing slot.
        if (startAt >= ev.EndAt)
            return Result.Failure<Unit>(new Error(
                "Shift.StartAfterEvent",
                "Shift start time must be before the event end time."));

        // End time rules.
        if (endAt is { } end)
        {
            // Clean, user-friendly version of the entity invariant. We
            // catch this here so the UI gets a contextual sentence
            // instead of the raw .NET ArgumentException param tail.
            if (end <= startAt)
                return Result.Failure<Unit>(new Error(
                    "Shift.EndBeforeStart",
                    "Shift end time must be after the shift start time."));

            // Upper bound on end: teardown can stay late but not absurdly.
            if (end > ev.EndAt + PostEventPad)
                return Result.Failure<Unit>(new Error(
                    "Shift.EndTooLate",
                    $"Shift end time can't be more than {PostEventPad.TotalHours:F0}h after the event end time."));

            // No lower bound on EndAt beyond "end > start" — a box office
            // shift that closes before doors open is fine.
        }

        return Result.Success(Unit.Value);
    }
}
