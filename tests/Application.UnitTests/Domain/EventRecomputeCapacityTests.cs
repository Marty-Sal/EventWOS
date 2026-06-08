using EventWOS.Domain.Entities;
using FluentAssertions;
using Xunit;

namespace EventWOS.Application.UnitTests.Domain;

/// <summary>
/// Pins <see cref="Event.RecomputeCapacityFromShifts"/> — the helper that
/// keeps the legacy MaxCrew field in sync with SUM(active shifts.CrewCount)
/// after a Phase D shift add/edit/archive.
///
/// The shrink floor matters most: a malicious or stale client could
/// otherwise drive event capacity below the number of approved crew via
/// a shift edit, and we'd silently violate the headline invariant.
/// </summary>
public sealed class EventRecomputeCapacityTests
{
    private static Event Make(int initialCapacity = 10) =>
        new(
            title: "Sample",
            description: null,
            venue: "Hall A",
            address: null,
            startAt: new DateTime(2026, 12, 01, 18, 00, 00, DateTimeKind.Utc),
            endAt:   new DateTime(2026, 12, 01, 23, 00, 00, DateTimeKind.Utc),
            createdByUserId: Guid.NewGuid(),
            maxCrew: initialCapacity);

    [Fact]
    public void RecomputeCapacity_grow_updates_MaxCrew()
    {
        var ev = Make(initialCapacity: 5);
        ev.RecomputeCapacityFromShifts(newTotal: 12);
        ev.MaxCrew.Should().Be(12);
    }

    [Fact]
    public void RecomputeCapacity_shrink_above_floor_is_ok()
    {
        var ev = Make(initialCapacity: 20);
        ev.RecomputeCapacityFromShifts(newTotal: 8, currentSeatsOccupied: 5);
        ev.MaxCrew.Should().Be(8);
    }

    [Fact]
    public void RecomputeCapacity_shrink_below_seats_occupied_throws()
    {
        var ev = Make(initialCapacity: 20);
        var act = () => ev.RecomputeCapacityFromShifts(newTotal: 3, currentSeatsOccupied: 5);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*5*");
    }

    [Fact]
    public void RecomputeCapacity_exactly_at_floor_is_ok()
    {
        var ev = Make(initialCapacity: 20);
        ev.RecomputeCapacityFromShifts(newTotal: 5, currentSeatsOccupied: 5);
        ev.MaxCrew.Should().Be(5);
    }

    [Fact]
    public void RecomputeCapacity_negative_total_throws()
    {
        var ev = Make();
        var act = () => ev.RecomputeCapacityFromShifts(newTotal: -1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void RecomputeCapacity_no_seats_occupied_lets_shrink_to_zero()
    {
        // Defensive: zero-capacity isn't a valid product state, but the
        // recompute itself shouldn't block it — the handler's "last shift"
        // guard owns that rule.
        var ev = Make(initialCapacity: 10);
        ev.RecomputeCapacityFromShifts(newTotal: 0, currentSeatsOccupied: 0);
        ev.MaxCrew.Should().Be(0);
    }

    [Fact]
    public void RecomputeCapacity_bumps_UpdatedAt()
    {
        var ev = Make();
        ev.RecomputeCapacityFromShifts(newTotal: 15);
        ev.UpdatedAt.Should().NotBeNull();
        ev.UpdatedAt!.Value.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));
    }
}
