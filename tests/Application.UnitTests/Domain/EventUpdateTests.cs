using EventWOS.Domain.Entities;
using FluentAssertions;
using Xunit;

namespace EventWOS.Application.UnitTests.Domain;

/// <summary>
/// Pins the invariants on <see cref="Event.Update"/>. These rules are
/// load-bearing — they're what the API relies on to reject malicious
/// or stale clients that try to shrink staffing below approved counts,
/// or edit a finished event.
///
/// (Lives in the Application test project for now — we'll spin up a
/// dedicated Domain.UnitTests project the moment we have a second
/// domain-level rule worth a whole project. Single test project keeps
/// the test surface lean while there's only a handful of tests.)
/// </summary>
public sealed class EventUpdateTests
{
    private static Event NewDraftEvent(int maxCrew = 10) =>
        new(
            title: "Sample",
            description: null,
            venue: "Hall A",
            address: null,
            startAt: new DateTime(2026, 12, 01, 18, 00, 00, DateTimeKind.Utc),
            endAt:   new DateTime(2026, 12, 01, 23, 00, 00, DateTimeKind.Utc),
            createdByUserId: Guid.NewGuid(),
            maxCrew: maxCrew);

    [Fact]
    public void Update_changes_editable_fields()
    {
        var ev = NewDraftEvent(maxCrew: 10);

        ev.Update(
            title: "Renamed",
            description: "Now with description",
            venue: "Hall B",
            address: "123 Main",
            startAt: new DateTime(2026, 12, 02, 18, 00, 00, DateTimeKind.Utc),
            endAt:   new DateTime(2026, 12, 02, 23, 00, 00, DateTimeKind.Utc),
            maxCrew: 15,
            currentSeatsOccupied: 0);

        ev.Title.Should().Be("Renamed");
        ev.Venue.Should().Be("Hall B");
        ev.MaxCrew.Should().Be(15);
    }

    [Fact]
    public void Update_throws_when_event_is_completed()
    {
        var ev = NewDraftEvent();
        // Drive it to Completed via the legal lifecycle.
        ev.Publish(); ev.Start(); ev.Complete();

        var act = () => ev.Update("X", null, "X", null,
                                  ev.StartAt, ev.EndAt, ev.MaxCrew, 0);
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*Completed*");
    }

    [Fact]
    public void Update_throws_when_event_is_cancelled()
    {
        var ev = NewDraftEvent();
        ev.Cancel();

        var act = () => ev.Update("X", null, "X", null,
                                  ev.StartAt, ev.EndAt, ev.MaxCrew, 0);
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*Cancelled*");
    }

    // ── The Phase 6 rule the user asked for ──────────────────────────────────

    [Fact]
    public void Update_throws_when_shrinking_below_approved_count()
    {
        var ev = NewDraftEvent(maxCrew: 10);

        // Pretend 5 crew already occupy a seat. Trying to set MaxCrew to 4
        // must throw with the exact "already approved" copy the UI shows.
        var act = () => ev.Update(ev.Title, ev.Description, ev.Venue, ev.Address,
                                  ev.StartAt, ev.EndAt,
                                  maxCrew: 4,
                                  currentSeatsOccupied: 5);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*5*already approved*");
    }

    [Fact]
    public void Update_allows_shrinking_to_exactly_the_approved_count()
    {
        var ev = NewDraftEvent(maxCrew: 10);

        // Setting MaxCrew to 5 with 5 approved is the boundary — must succeed.
        ev.Update(ev.Title, ev.Description, ev.Venue, ev.Address,
                  ev.StartAt, ev.EndAt,
                  maxCrew: 5,
                  currentSeatsOccupied: 5);

        ev.MaxCrew.Should().Be(5);
    }

    [Fact]
    public void Update_treats_max_crew_zero_as_unlimited()
    {
        // MaxCrew == 0 means "no cap" by historical convention in this codebase
        // — the floor check should be skipped entirely.
        var ev = NewDraftEvent(maxCrew: 10);

        ev.Update(ev.Title, ev.Description, ev.Venue, ev.Address,
                  ev.StartAt, ev.EndAt,
                  maxCrew: 0,
                  currentSeatsOccupied: 50);

        ev.MaxCrew.Should().Be(0);
    }
}
