using EventWOS.Domain.Entities;
using FluentAssertions;
using Xunit;

namespace EventWOS.Application.UnitTests.Shifts;

/// <summary>
/// Pins the per-shift time bounds rule. Updated 2026-06-09 after user
/// feedback: shifts may now START up to 24h BEFORE the event (load-in)
/// and END up to 24h AFTER the event (teardown). Outside that 24h pad,
/// it's almost certainly a typo and we surface a clear error.
/// </summary>
public sealed class ShiftTimeBoundsTests
{
    private static Event MakeEvent() =>
        new(
            title: "Sample",
            description: null,
            venue: "Hall A",
            address: null,
            startAt: new DateTime(2026, 12, 01, 18, 00, 00, DateTimeKind.Utc),
            endAt:   new DateTime(2026, 12, 01, 23, 00, 00, DateTimeKind.Utc),
            createdByUserId: Guid.NewGuid(),
            maxCrew: 10);

    [Fact]
    public void Shift_inside_event_window_is_ok()
    {
        var ev = MakeEvent();
        var r = EventWOS.Application.Events.Shifts.ShiftTimeBounds.Validate(
            ev,
            startAt: new DateTime(2026, 12, 01, 19, 00, 00, DateTimeKind.Utc),
            endAt:   new DateTime(2026, 12, 01, 22, 00, 00, DateTimeKind.Utc));
        r.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Shift_starts_exactly_at_event_start_is_ok()
    {
        var ev = MakeEvent();
        var r = EventWOS.Application.Events.Shifts.ShiftTimeBounds.Validate(
            ev, startAt: ev.StartAt, endAt: null);
        r.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Shift_starts_a_few_hours_before_event_is_ok_loadin()
    {
        // Realistic load-in: setup crew at 14:00 for an 18:00 doors-open.
        var ev = MakeEvent();
        var r = EventWOS.Application.Events.Shifts.ShiftTimeBounds.Validate(
            ev,
            startAt: ev.StartAt.AddHours(-4),
            endAt:   ev.StartAt.AddHours(-1));
        r.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Shift_starts_exactly_pad_before_event_is_ok()
    {
        // Boundary: the lower edge of the pre-event pad.
        var ev = MakeEvent();
        var r = EventWOS.Application.Events.Shifts.ShiftTimeBounds.Validate(
            ev,
            startAt: ev.StartAt - EventWOS.Application.Events.Shifts.ShiftTimeBounds.PreEventPad,
            endAt:   ev.StartAt);
        r.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Shift_starts_more_than_pad_before_event_fails()
    {
        // Almost certainly a typo (e.g. picked Nov 30 instead of Dec 1).
        var ev = MakeEvent();
        var r = EventWOS.Application.Events.Shifts.ShiftTimeBounds.Validate(
            ev,
            startAt: ev.StartAt - EventWOS.Application.Events.Shifts.ShiftTimeBounds.PreEventPad - TimeSpan.FromMinutes(1),
            endAt:   ev.StartAt);
        r.IsFailure.Should().BeTrue();
        r.Error.Code.Should().Be("Shift.StartTooEarly");
    }

    [Fact]
    public void Shift_starts_at_event_end_fails()
    {
        // Strict less-than: a shift starting at the moment the event ends
        // has zero useful staffing window.
        var ev = MakeEvent();
        var r = EventWOS.Application.Events.Shifts.ShiftTimeBounds.Validate(
            ev, startAt: ev.EndAt, endAt: null);
        r.IsFailure.Should().BeTrue();
        r.Error.Code.Should().Be("Shift.StartAfterEvent");
    }

    [Fact]
    public void Shift_ends_a_few_hours_after_event_is_ok_teardown()
    {
        // Realistic teardown: stage breakdown until 02:00 for a 23:00 doors-close.
        var ev = MakeEvent();
        var r = EventWOS.Application.Events.Shifts.ShiftTimeBounds.Validate(
            ev,
            startAt: ev.EndAt.AddHours(-1),
            endAt:   ev.EndAt.AddHours(3));
        r.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Shift_ends_exactly_pad_after_event_is_ok()
    {
        var ev = MakeEvent();
        var r = EventWOS.Application.Events.Shifts.ShiftTimeBounds.Validate(
            ev,
            startAt: ev.StartAt,
            endAt:   ev.EndAt + EventWOS.Application.Events.Shifts.ShiftTimeBounds.PostEventPad);
        r.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Shift_ends_more_than_pad_after_event_fails()
    {
        var ev = MakeEvent();
        var r = EventWOS.Application.Events.Shifts.ShiftTimeBounds.Validate(
            ev,
            startAt: ev.StartAt,
            endAt:   ev.EndAt + EventWOS.Application.Events.Shifts.ShiftTimeBounds.PostEventPad + TimeSpan.FromMinutes(1));
        r.IsFailure.Should().BeTrue();
        r.Error.Code.Should().Be("Shift.EndTooLate");
    }

    [Fact]
    public void Null_end_is_ok()
    {
        var ev = MakeEvent();
        var r = EventWOS.Application.Events.Shifts.ShiftTimeBounds.Validate(
            ev, startAt: ev.StartAt.AddMinutes(30), endAt: null);
        r.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Shift_ends_before_event_starts_is_ok_boxoffice()
    {
        // Box office shift: opens for load-in, closes once doors open and
        // all guests are inside. End time is BEFORE event.StartAt. Allowed.
        var ev = MakeEvent();
        var r = EventWOS.Application.Events.Shifts.ShiftTimeBounds.Validate(
            ev,
            startAt: ev.StartAt.AddHours(-6),
            endAt:   ev.StartAt.AddHours(-1));
        r.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Shift_end_before_or_equal_start_fails_cleanly()
    {
        // Catches the screenshot bug: user picked Jun 9 for end but Jun 10
        // for start. Surface a friendly message via ShiftTimeBounds rather
        // than letting the entity throw an ArgumentException with the raw
        // (Parameter 'endAt') tail.
        var ev = MakeEvent();
        var r = EventWOS.Application.Events.Shifts.ShiftTimeBounds.Validate(
            ev,
            startAt: ev.StartAt,
            endAt:   ev.StartAt);
        r.IsFailure.Should().BeTrue();
        r.Error.Code.Should().Be("Shift.EndBeforeStart");
    }
}
