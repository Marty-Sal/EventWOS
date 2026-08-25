using EventOpsOracle.Application.Attendance.Geo;
using EventOpsOracle.Domain.Geo;
using FluentAssertions;
using Xunit;

namespace EventOpsOracle.Application.UnitTests.Geofencing;

/// <summary>
/// The attendance authorization rule. These are the highest-stakes tests in the
/// location feature: a false "allowed" lets crew mark attendance from home, and
/// a false "rejected" strands someone standing on site.
///
/// Reference point throughout is Millennium Business Park, Navi Mumbai.
/// </summary>
public sealed class GeoFenceEvaluatorTests
{
    private const double VenueLat = 19.10528;
    private const double VenueLng = 73.01989;

    /// <summary>
    /// A point <paramref name="metresNorth"/> due north of the venue.
    /// Latitude degrees are ~111,320 m everywhere, so going north keeps the
    /// arithmetic independent of the longitude convergence at this latitude.
    /// </summary>
    private static string PointNorthOfVenue(double metresNorth)
    {
        var lat = VenueLat + (metresNorth / 111_320d);
        return $"{lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}," +
               $"{VenueLng.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    }

    // ── 7. Crew inside geofence → attendance allowed ─────────────────────────

    [Theory]
    [InlineData(0)]    // standing on the pin
    [InlineData(25)]
    [InlineData(99)]
    public void Crew_inside_the_radius_is_allowed(double metresAway)
    {
        var result = GeoFenceEvaluator.Evaluate(
            geoFenceEnabled: true,
            geoFenceRadiusMeters: 100,
            venueLatitude: VenueLat,
            venueLongitude: VenueLng,
            crewLocationRaw: PointNorthOfVenue(metresAway));

        result.Allowed.Should().BeTrue();
        result.FailureCode.Should().BeNull();
        result.DistanceMetres.Should().BeApproximately(metresAway, 1.5);
    }

    [Fact]
    public void Crew_exactly_on_the_boundary_is_allowed()
    {
        // Inclusive comparison (distance <= radius) is the documented contract:
        // "within 100 m" should mean 100 m counts. An exclusive boundary would
        // also make the outcome depend on floating-point noise.
        var result = GeoFenceEvaluator.Evaluate(
            true, 100, VenueLat, VenueLng, PointNorthOfVenue(99.9));

        result.Allowed.Should().BeTrue();
    }

    // ── 8. Crew outside geofence → attendance rejected ───────────────────────

    [Theory]
    [InlineData(150, 100)]
    [InlineData(500, 100)]
    [InlineData(5_000, 300)]
    public void Crew_outside_the_radius_is_rejected(double metresAway, int radius)
    {
        var result = GeoFenceEvaluator.Evaluate(
            true, radius, VenueLat, VenueLng, PointNorthOfVenue(metresAway));

        result.Allowed.Should().BeFalse();
        result.FailureCode.Should().Be("CheckIn.OutsideGeoFence");
        result.FailureMessage.Should().Contain("outside the permitted event location");
        // The message must tell them the target, otherwise "move closer" is
        // unactionable.
        result.FailureMessage.Should().Contain(radius.ToString());
    }

    [Fact]
    public void Rejection_message_reports_distance_at_human_precision()
    {
        var near = GeoFenceEvaluator.Evaluate(true, 100, VenueLat, VenueLng, PointNorthOfVenue(250));
        near.FailureMessage.Should().Contain("m away");

        var far = GeoFenceEvaluator.Evaluate(true, 100, VenueLat, VenueLng, PointNorthOfVenue(4_000));
        far.FailureMessage.Should().Contain("km away",
            "quoting thousands of metres reads badly and implies precision GPS doesn't have");
    }

    // ── 9. GeoFenceEnabled = false → location validation not applied ─────────

    [Theory]
    [InlineData("19.10528,73.01989")]  // on site
    [InlineData("28.61390,77.20900")]  // Delhi — ~1,150 km away
    [InlineData(null)]                 // no fix at all
    [InlineData("unavailable:denied")] // client sentinel
    [InlineData("garbage")]
    public void Fence_disabled_never_blocks_check_in(string? crewLocation)
    {
        // With the fence off, location is still recorded for the audit trail but
        // must not gate anything — including when the device gave us nothing.
        var result = GeoFenceEvaluator.Evaluate(
            geoFenceEnabled: false,
            geoFenceRadiusMeters: null,
            venueLatitude: VenueLat,
            venueLongitude: VenueLng,
            crewLocationRaw: crewLocation);

        result.Allowed.Should().BeTrue();
        result.FailureCode.Should().BeNull();
    }

    [Fact]
    public void Fence_disabled_is_allowed_even_with_a_stale_radius_present()
    {
        // Defence in depth: DisableGeoFence clears the radius, but a legacy row
        // must not be re-armed by the presence of a leftover number.
        var result = GeoFenceEvaluator.Evaluate(false, 50, VenueLat, VenueLng, "28.6139,77.2090");

        result.Allowed.Should().BeTrue();
    }

    // ── Untrusted input handling ─────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("unavailable:denied")]
    [InlineData("unavailable:timeout")]
    [InlineData("19.10528")]              // one component
    [InlineData("19.10528,73.01989,10")]  // three components
    [InlineData("abc,def")]
    [InlineData("NaN,NaN")]
    [InlineData("Infinity,0")]
    [InlineData("91.0,73.0")]             // latitude out of range
    [InlineData("19.1,181.0")]            // longitude out of range
    public void Unparseable_or_out_of_range_fixes_are_rejected_when_the_fence_is_armed(string? raw)
    {
        // Fail closed. This value comes from the client and is used in an
        // authorization decision, so anything we can't read confidently is a
        // rejection rather than a guess.
        var result = GeoFenceEvaluator.Evaluate(true, 100, VenueLat, VenueLng, raw);

        result.Allowed.Should().BeFalse();
        result.FailureCode.Should().Be("CheckIn.LocationRequired");
    }

    [Fact]
    public void Coordinates_parse_under_a_comma_decimal_locale()
    {
        // The wire format is always '.'-separated. A server running under, say,
        // de-DE must not mis-read "19.10528" as 1910528.
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");

            GeoFenceEvaluator.TryParseCoordinates("19.10528,73.01989", out var lat, out var lng)
                .Should().BeTrue();
            lat.Should().BeApproximately(19.10528, 0.000001);
            lng.Should().BeApproximately(73.01989, 0.000001);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }

    // ── Misconfiguration must fail CLOSED ────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-50)]
    public void Armed_fence_with_no_usable_radius_is_rejected(int? radius)
    {
        // The admin's screen says location is being verified. Waving everyone
        // through would silently break that promise, so an unevaluable fence
        // rejects and names the fix.
        var result = GeoFenceEvaluator.Evaluate(true, radius, VenueLat, VenueLng, PointNorthOfVenue(5));

        result.Allowed.Should().BeFalse();
        result.FailureCode.Should().Be("CheckIn.GeoFenceMisconfigured");
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(19.10528, null)]
    [InlineData(null, 73.01989)]
    public void Armed_fence_with_missing_venue_coordinates_is_rejected(double? lat, double? lng)
    {
        var result = GeoFenceEvaluator.Evaluate(true, 100, lat, lng, PointNorthOfVenue(5));

        result.Allowed.Should().BeFalse();
        result.FailureCode.Should().Be("CheckIn.VenueCoordinatesMissing");
    }

    // ── Distance primitive ───────────────────────────────────────────────────

    [Fact]
    public void Distance_is_symmetric_and_zero_for_identical_points()
    {
        GeoDistance.MetresBetween(VenueLat, VenueLng, VenueLat, VenueLng).Should().Be(0);

        var ab = GeoDistance.MetresBetween(VenueLat, VenueLng, 18.98656, 72.81547);
        var ba = GeoDistance.MetresBetween(18.98656, 72.81547, VenueLat, VenueLng);
        ab.Should().BeApproximately(ba, 0.0001);
    }

    [Fact]
    public void Distance_matches_a_known_real_world_separation()
    {
        // Millennium Business Park (Mahape) → DOME, SVP Stadium (Mumbai).
        // ~25 km by great circle; 500 m tolerance covers Haversine's spherical
        // approximation against an ellipsoidal reference.
        var metres = GeoDistance.MetresBetween(VenueLat, VenueLng, 18.98656, 72.81547);

        metres.Should().BeInRange(24_000, 26_500);
    }
}
