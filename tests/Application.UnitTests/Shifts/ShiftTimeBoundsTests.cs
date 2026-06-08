using EventWOS.Domain.Entities;
using FluentAssertions;
using Xunit;

namespace EventWOS.Application.UnitTests.Shifts;

/// <summary>
/// Pins the per-shift time bounds rule introduced in Phase D step 2.
/// Shifts must fit inside [event.StartAt, event.EndAt) — staffing slots
/// outside the event window are nonsensical and would confuse downstream
/// timesheet / payment views.
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
    public void Shift_starts_before_event_fails()
    {
        var ev = MakeEvent();
        var r = EventWOS.Application.Events.Shifts.ShiftTimeBounds.Validate(
            ev,
            startAt: ev.StartAt.AddMinutes(-1),
            endAt:   null);
        r.IsFailure.Should().BeTrue();
        r.Error.Code.Should().Be("Shift.StartBeforeEvent");
    }

    [Fact]
    public void Shift_starts_at_event_end_fails()
    {
        // Strict less-than: a shift starting at the exact moment the event
        // ends has zero useful staffing window.
        var ev = MakeEvent();
        var r = EventWOS.Application.Events.Shifts.ShiftTimeBounds.Validate(
            ev, startAt: ev.EndAt, endAt: null);
        r.IsFailure.Should().BeTrue();
        r.Error.Code.Should().Be("Shift.StartAfterEvent");
    }

    [Fact]
    public void Shift_ends_after_event_fails()
    {
        var ev = MakeEvent();
        var r = EventWOS.Application.Events.Shifts.ShiftTimeBounds.Validate(
            ev,
            startAt: ev.StartAt,
            endAt:   ev.EndAt.AddMinutes(1));
        r.IsFailure.Should().BeTrue();
        r.Error.Code.Should().Be("Shift.EndAfterEvent");
    }

    [Fact]
    public void Shift_ends_exactly_at_event_end_is_ok()
    {
        var ev = MakeEvent();
        var r = EventWOS.Application.Events.Shifts.ShiftTimeBounds.Validate(
            ev, startAt: ev.StartAt, endAt: ev.EndAt);
        r.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Null_end_is_ok()
    {
        var ev = MakeEvent();
        var r = EventWOS.Application.Events.Shifts.ShiftTimeBounds.Validate(
            ev, startAt: ev.StartAt.AddMinutes(30), endAt: null);
        r.IsSuccess.Should().BeTrue();
    }
}
