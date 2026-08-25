namespace EventOpsOracle.Domain.Geo;

/// <summary>
/// Great-circle distance between two WGS-84 points, in metres.
///
/// Lives in Domain (not Infrastructure) because "is this crew member inside
/// the event's geofence?" is a business rule, not an integration detail — it
/// must be computable without a database, an HTTP client, or a map provider,
/// and it must be unit-testable in isolation.
///
/// Haversine with a spherical earth is the right tool here: at geofence
/// scale (tens to a few hundred metres) the spherical approximation is off by
/// well under a metre versus a full ellipsoidal (Vincenty) calculation, which
/// is far below consumer GPS accuracy (typically 5–20 m). Using Vincenty would
/// add iteration and edge-case convergence bugs to buy precision the input
/// data doesn't have.
/// </summary>
public static class GeoDistance
{
    /// <summary>
    /// IUGG mean earth radius in metres. Using the mean (not equatorial)
    /// radius keeps the error symmetric across latitudes.
    /// </summary>
    private const double EarthRadiusMetres = 6_371_008.8;

    /// <summary>
    /// Distance in metres between two points. Always non-negative, and
    /// symmetric in its arguments.
    /// </summary>
    public static double MetresBetween(
        double latitude1, double longitude1,
        double latitude2, double longitude2)
    {
        // Identical points: short-circuit. Guards against the tiny negative
        // values that can fall out of the sqrt for coincident coordinates.
        if (latitude1 == latitude2 && longitude1 == longitude2) return 0d;

        var lat1Rad = ToRadians(latitude1);
        var lat2Rad = ToRadians(latitude2);
        var deltaLat = ToRadians(latitude2 - latitude1);
        var deltaLon = ToRadians(longitude2 - longitude1);

        var sinHalfDeltaLat = Math.Sin(deltaLat / 2);
        var sinHalfDeltaLon = Math.Sin(deltaLon / 2);

        var a = (sinHalfDeltaLat * sinHalfDeltaLat)
              + (Math.Cos(lat1Rad) * Math.Cos(lat2Rad) * sinHalfDeltaLon * sinHalfDeltaLon);

        // Clamp before the sqrt: accumulated floating-point error can push `a`
        // a hair above 1 for antipodal points, which would make Asin/Sqrt
        // produce NaN and silently turn a geofence check into "not inside".
        a = Math.Clamp(a, 0d, 1d);

        var c = 2 * Math.Asin(Math.Sqrt(a));
        return EarthRadiusMetres * c;
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180d;
}
