using System.Reflection;
using EventOpsOracle.Application.Attendance.Commands;
using EventOpsOracle.Application.Attendance.Geo;
using FluentAssertions;
using Xunit;

namespace EventOpsOracle.Application.UnitTests.Geofencing;

/// <summary>
/// Scenario 10 — a client-supplied radius must never override
/// Event.GeoFenceRadiusMeters.
///
/// This is enforced structurally rather than by validation: the attendance
/// commands simply have no radius parameter, so there is no value for a
/// malicious client to send. These tests lock that shape in, because the
/// realistic regression is someone "helpfully" adding a radius field to the
/// request later — validation you can forget to call, a missing parameter you
/// cannot.
/// </summary>
public sealed class ClientCannotOverrideGeoFenceTests
{
    private const double VenueLat = 19.10528;
    private const double VenueLng = 73.01989;

    /// <summary>A point ~500 m north of the venue — outside any sane fence.</summary>
    private const string FarAwayFix = "19.109772,73.019890";

    [Fact]
    public void Attendance_commands_expose_no_radius_parameter_for_a_client_to_set()
    {
        var commandTypes = new[]
        {
            typeof(RecordAttendanceCommand),
            typeof(RequestCheckInCommand),
            typeof(VerifyCheckInCommand),
        };

        foreach (var type in commandTypes)
        {
            var suspicious = type.GetProperties()
                .Where(p => p.Name.Contains("Radius", StringComparison.OrdinalIgnoreCase)
                         || p.Name.Contains("GeoFence", StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Name)
                .ToList();

            suspicious.Should().BeEmpty(
                $"{type.Name} must not accept geofence configuration from the caller — " +
                "the radius is read from the Event row on the server");
        }
    }

    [Fact]
    public void Evaluator_takes_the_radius_as_a_server_supplied_argument_only()
    {
        // The signature IS the contract: the radius arrives as an int? the
        // handler loaded from the database, and the only string (untrusted)
        // input is the crew's own GPS fix.
        var method = typeof(GeoFenceEvaluator).GetMethod(
            nameof(GeoFenceEvaluator.Evaluate), BindingFlags.Public | BindingFlags.Static);

        method.Should().NotBeNull();

        var parameters = method!.GetParameters();
        parameters.Should().HaveCount(5);
        parameters[0].Name.Should().Be("geoFenceEnabled");
        parameters[1].Name.Should().Be("geoFenceRadiusMeters");
        parameters[4].Name.Should().Be("crewLocationRaw");

        parameters.Count(p => p.ParameterType == typeof(string))
            .Should().Be(1, "exactly one parameter carries untrusted client input");
    }

    [Fact]
    public void A_generous_radius_a_client_might_wish_for_has_no_effect_on_the_decision()
    {
        // Simulate the attack: the client wants a 10 km fence so it can check in
        // from home. The evaluator is only ever handed the EVENT's radius, so
        // the wished-for value is inert — the same fix is rejected under the
        // event's 100 m and would only pass if the EVENT itself said 10 km.
        const int eventConfiguredRadius = 100;
        const int radiusTheClientWants  = 10_000;

        var enforced = GeoFenceEvaluator.Evaluate(
            geoFenceEnabled: true,
            geoFenceRadiusMeters: eventConfiguredRadius,
            venueLatitude: VenueLat,
            venueLongitude: VenueLng,
            crewLocationRaw: FarAwayFix);

        enforced.Allowed.Should().BeFalse();
        enforced.RadiusMetres.Should().Be(eventConfiguredRadius);
        enforced.FailureMessage.Should().Contain(eventConfiguredRadius.ToString());
        enforced.FailureMessage.Should().NotContain(radiusTheClientWants.ToString());

        // Sanity check that the fix really is inside the wished-for radius —
        // i.e. the rejection above is the event's radius doing the work, not an
        // accident of the test coordinates.
        var ifTheClientHadWon = GeoFenceEvaluator.Evaluate(
            true, radiusTheClientWants, VenueLat, VenueLng, FarAwayFix);
        ifTheClientHadWon.Allowed.Should().BeTrue();
    }

    [Fact]
    public void Crew_cannot_widen_the_fence_by_padding_the_location_string()
    {
        // Defensive: a client appending an accuracy/radius third component must
        // not be parsed leniently into something the evaluator accepts.
        var result = GeoFenceEvaluator.Evaluate(
            true, 100, VenueLat, VenueLng, $"{FarAwayFix},10000");

        result.Allowed.Should().BeFalse();
        result.FailureCode.Should().Be("CheckIn.LocationRequired");
    }
}
