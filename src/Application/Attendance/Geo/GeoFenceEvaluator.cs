using EventWOS.Domain.Geo;

namespace EventWOS.Application.Attendance.Geo;

/// <summary>
/// The single place where "is this crew member allowed to check in from here?"
/// is decided. Both attendance write-paths (RequestCheckInCommand, which mints
/// the QR, and RecordAttendanceCommand, which writes the record) call this, so
/// the rule cannot drift between them.
///
/// Everything it needs is passed in explicitly — no DbContext, no HTTP client,
/// no clock. That keeps it trivially unit-testable and, more importantly, makes
/// the trust boundary obvious at every call site: the venue coordinates and the
/// radius are arguments the CALLER must have loaded from the database, and the
/// only client-supplied value is the raw GPS fix.
/// </summary>
public static class GeoFenceEvaluator
{
    /// <summary>
    /// Outcome of a geofence evaluation. <see cref="Allowed"/> is the only
    /// field callers need for the decision; the rest is for messaging and logs.
    /// </summary>
    public sealed record GeoFenceCheck(
        bool    Allowed,
        string? FailureCode,
        string? FailureMessage,
        double? DistanceMetres,
        int?    RadiusMetres)
    {
        public static GeoFenceCheck Pass(double? distance = null, int? radius = null)
            => new(true, null, null, distance, radius);

        public static GeoFenceCheck Fail(string code, string message, double? distance = null, int? radius = null)
            => new(false, code, message, distance, radius);
    }

    /// <summary>
    /// Evaluate the fence.
    /// </summary>
    /// <param name="geoFenceEnabled">Event.GeoFenceEnabled — read from the DB.</param>
    /// <param name="geoFenceRadiusMeters">Event.GeoFenceRadiusMeters — read from the DB. NEVER from the request.</param>
    /// <param name="venueLatitude">Venue.Latitude — read from the DB.</param>
    /// <param name="venueLongitude">Venue.Longitude — read from the DB.</param>
    /// <param name="crewLocationRaw">The device's raw "lat,lng" fix. The ONLY client-supplied input.</param>
    public static GeoFenceCheck Evaluate(
        bool    geoFenceEnabled,
        int?    geoFenceRadiusMeters,
        double? venueLatitude,
        double? venueLongitude,
        string? crewLocationRaw)
    {
        // Fence off → this check has no opinion. Location is still captured on
        // the attendance record for the audit trail, it just doesn't gate.
        if (!geoFenceEnabled)
            return GeoFenceCheck.Pass();

        // Misconfiguration. Event.EnableGeoFence and the DB CHECK constraint
        // both prevent this state, so reaching here means something bypassed
        // them (a manual SQL update, say). Fail CLOSED: an armed fence we
        // cannot evaluate must not silently become "everyone welcome", because
        // the admin's screen says location is being verified.
        if (geoFenceRadiusMeters is null or <= 0)
            return GeoFenceCheck.Fail(
                "CheckIn.GeoFenceMisconfigured",
                "Location verification is enabled for this event but no radius is configured. Ask your administrator to set the geofence radius.");

        if (venueLatitude is null || venueLongitude is null)
            return GeoFenceCheck.Fail(
                "CheckIn.VenueCoordinatesMissing",
                "Location verification is enabled but this event's venue has no coordinates saved. Ask your administrator to set the venue location.");

        if (!TryParseCoordinates(crewLocationRaw, out var crewLat, out var crewLng))
            return GeoFenceCheck.Fail(
                "CheckIn.LocationRequired",
                "Location is required to check in. Please enable location access and try again.");

        var distance = GeoDistance.MetresBetween(
            venueLatitude.Value, venueLongitude.Value, crewLat, crewLng);

        if (distance > geoFenceRadiusMeters.Value)
        {
            // Round for the message: quoting "you are 213.847 m away" implies a
            // precision the GPS fix doesn't have.
            var away = distance >= 1000
                ? $"{distance / 1000:0.0} km"
                : $"{Math.Round(distance)} m";

            return GeoFenceCheck.Fail(
                "CheckIn.OutsideGeoFence",
                $"You are outside the permitted event location. You need to be within {geoFenceRadiusMeters.Value} m of the venue — you are currently about {away} away.",
                distance, geoFenceRadiusMeters);
        }

        return GeoFenceCheck.Pass(distance, geoFenceRadiusMeters);
    }

    /// <summary>
    /// Parse the browser's "lat,lng" string. Strict on shape and range: this is
    /// untrusted input used in an authorization decision, so anything we can't
    /// confidently read becomes a rejection rather than a guess.
    ///
    /// Also rejects the "unavailable:*" sentinels the client sends when the
    /// device has no fix — those must not be mistaken for coordinates.
    /// </summary>
    public static bool TryParseCoordinates(string? raw, out double latitude, out double longitude)
    {
        latitude  = 0;
        longitude = 0;

        if (string.IsNullOrWhiteSpace(raw)) return false;

        var parts = raw.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 2) return false;

        // InvariantCulture explicitly: the wire format always uses '.' as the
        // decimal separator, and a server running under a comma-decimal locale
        // would otherwise mis-parse every fix.
        if (!double.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var lat))
            return false;
        if (!double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var lng))
            return false;

        if (double.IsNaN(lat) || double.IsNaN(lng)
            || double.IsInfinity(lat) || double.IsInfinity(lng))
            return false;

        if (lat is < -90 or > 90 || lng is < -180 or > 180) return false;

        latitude  = lat;
        longitude = lng;
        return true;
    }
}
