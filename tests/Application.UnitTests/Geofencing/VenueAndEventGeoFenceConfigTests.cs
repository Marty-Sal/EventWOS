using EventOpsOracle.Domain.Entities;
using FluentAssertions;
using Xunit;

namespace EventOpsOracle.Application.UnitTests.Geofencing;

/// <summary>
/// Venue coordinate persistence and Event geofence configuration — the two
/// halves of the "who owns what" split: Venue owns the confirmed physical
/// point, Event owns the tolerance around it.
/// </summary>
public sealed class VenueAndEventGeoFenceConfigTests
{
    private static Venue MakeVenue(double? lat = 19.10528, double? lng = 73.01989)
        => new(
            name: "Millennium Business Park",
            addressLine1: "Mahape MIDC",
            addressLine2: null,
            shortAddress: "Mahape, Navi Mumbai, Maharashtra",
            city: "Navi Mumbai",
            state: "Maharashtra",
            postalCode: "400710",
            country: "India",
            latitude: lat,
            longitude: lng,
            notes: null,
            createdByUserId: Guid.NewGuid());

    private static Event MakeEvent(Guid? venueId)
        => new(
            title: "Product Launch",
            description: null,
            venue: "Millennium Business Park",
            address: "Mahape MIDC, Navi Mumbai",
            startAt: DateTime.UtcNow.AddDays(3),
            endAt: DateTime.UtcNow.AddDays(3).AddHours(6),
            createdByUserId: Guid.NewGuid(),
            maxCrew: 10,
            venueId: venueId);

    // ── 5. Venue coordinate persistence ──────────────────────────────────────

    [Fact]
    public void Venue_keeps_the_confirmed_coordinates_and_short_address()
    {
        var venue = MakeVenue();

        venue.Latitude.Should().Be(19.10528);
        venue.Longitude.Should().Be(73.01989);
        venue.ShortAddress.Should().Be("Mahape, Navi Mumbai, Maharashtra");
    }

    [Fact]
    public void Venue_rounds_coordinates_to_six_decimal_places()
    {
        // 6 dp is ~11 cm — already finer than consumer GPS. Storing more digits
        // implies accuracy the geocoder never had and makes cache keys and
        // equality checks unstable.
        var venue = MakeVenue(19.105281234567, 73.019894987654);

        venue.Latitude.Should().Be(19.105281);
        venue.Longitude.Should().Be(73.019895);
    }

    [Fact]
    public void Venue_updates_coordinates_when_the_pin_is_dragged()
    {
        var venue = MakeVenue();

        venue.Update(
            "Millennium Business Park", "Mahape MIDC", null,
            "Rabale, Navi Mumbai, Maharashtra",
            "Navi Mumbai", "Maharashtra", "400710", "India",
            19.123456, 73.654321, null);

        venue.Latitude.Should().Be(19.123456);
        venue.Longitude.Should().Be(73.654321);
        venue.ShortAddress.Should().Be("Rabale, Navi Mumbai, Maharashtra");
    }

    [Theory]
    [InlineData(91.0, 73.0)]
    [InlineData(-91.0, 73.0)]
    [InlineData(19.1, 181.0)]
    [InlineData(19.1, -181.0)]
    public void Venue_rejects_out_of_range_coordinates(double lat, double lng)
    {
        var act = () => MakeVenue(lat, lng);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Venue_allows_no_coordinates_yet()
    {
        // Venues can be catalogued before anyone geocodes them; the geofence
        // path is what refuses to rely on such a venue.
        var venue = MakeVenue(null, null);

        venue.Latitude.Should().BeNull();
        venue.Longitude.Should().BeNull();
    }

    /// <summary>
    /// The architectural rule, asserted so a future refactor can't quietly
    /// break it: the radius belongs to the Event. If someone adds a
    /// GeoFenceRadius* property to Venue, this test fails and explains why.
    /// </summary>
    [Fact]
    public void Venue_does_not_own_a_geofence_radius()
    {
        typeof(Venue).GetProperties()
            .Select(p => p.Name)
            .Should().NotContain(n => n.Contains("Radius", StringComparison.OrdinalIgnoreCase),
                "the geofence radius is Event-level config — two events at one venue need different boundaries");
    }

    // ── 6. Event geofence configuration ──────────────────────────────────────

    [Fact]
    public void Event_defaults_to_no_geofence()
    {
        var ev = MakeEvent(Guid.NewGuid());

        ev.GeoFenceEnabled.Should().BeFalse();
        ev.GeoFenceRadiusMeters.Should().BeNull();
    }

    [Theory]
    [InlineData(20)]
    [InlineData(100)]
    [InlineData(150)]
    [InlineData(300)]
    [InlineData(5_000)]
    public void Event_arms_the_fence_at_the_configured_radius(int radius)
    {
        var ev = MakeEvent(Guid.NewGuid());

        ev.EnableGeoFence(radius, venueHasCoordinates: true);

        ev.GeoFenceEnabled.Should().BeTrue();
        ev.GeoFenceRadiusMeters.Should().Be(radius);
    }

    [Fact]
    public void Two_events_at_one_venue_can_hold_different_radii()
    {
        // The reason the radius is not on Venue: a single hall wants 100 m, a
        // stadium-wide festival at the same address wants 300 m.
        var venueId = Guid.NewGuid();
        var eventA  = MakeEvent(venueId);
        var eventB  = MakeEvent(venueId);

        eventA.EnableGeoFence(100, true);
        eventB.EnableGeoFence(300, true);

        eventA.GeoFenceRadiusMeters.Should().Be(100);
        eventB.GeoFenceRadiusMeters.Should().Be(300);
        eventA.VenueId.Should().Be(eventB.VenueId);
    }

    [Theory]
    [InlineData(19)]      // below the GPS-accuracy floor
    [InlineData(0)]
    [InlineData(-100)]
    [InlineData(5_001)]   // above the cap
    [InlineData(500_000)] // the classic typo
    public void Event_rejects_a_radius_outside_the_supported_range(int radius)
    {
        var ev = MakeEvent(Guid.NewGuid());

        var act = () => ev.EnableGeoFence(radius, venueHasCoordinates: true);

        act.Should().Throw<ArgumentOutOfRangeException>();
        ev.GeoFenceEnabled.Should().BeFalse("a rejected radius must not half-arm the fence");
    }

    [Fact]
    public void Event_cannot_arm_a_fence_without_a_saved_venue()
    {
        // Event creation must not invent a venue implicitly — venues are
        // centrally managed master data, and there'd be nothing to measure from.
        var ev = MakeEvent(venueId: null);

        var act = () => ev.EnableGeoFence(150, venueHasCoordinates: false);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*saved venue*");
    }

    [Fact]
    public void Event_cannot_arm_a_fence_when_the_venue_has_no_coordinates()
    {
        // An armed fence with nothing to measure from would have to reject every
        // check-in or wave everyone through. Both are worse than refusing here,
        // where the admin can still act on the message.
        var ev = MakeEvent(Guid.NewGuid());

        var act = () => ev.EnableGeoFence(150, venueHasCoordinates: false);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*no coordinates*");
    }

    [Fact]
    public void Disabling_the_fence_clears_the_radius()
    {
        // A disabled fence must not leave a stale number behind for someone to
        // misread as active.
        var ev = MakeEvent(Guid.NewGuid());
        ev.EnableGeoFence(150, true);

        ev.DisableGeoFence();

        ev.GeoFenceEnabled.Should().BeFalse();
        ev.GeoFenceRadiusMeters.Should().BeNull();
    }
}
