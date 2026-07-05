namespace EventWOS.Application.Attendance.Geo;

/// <summary>
/// Reverse-geocodes a client-supplied "lat,lng" string into a
/// (coords, short-address) pair suitable for persistence in
/// AttendanceRecord.LocationCoords / LocationAddress.
///
/// Backed by OpenStreetMap\'s Nominatim service (free, no API key
/// required). See <see cref="EventWOS.Infrastructure.Geo.GeoLocationService"/>
/// for the concrete impl, rate-limiting, and User-Agent policy notes.
/// </summary>
public interface IGeoLocationService
{
    /// <summary>
    /// Takes a raw "lat,lng" fix as sent by the browser and returns
    /// a normalised pair for the DB columns:
    ///   * <c>Coords</c>: the same "lat,lng" (6 decimal places) if
    ///     parseable; otherwise <c>null</c>.
    ///   * <c>Address</c>: a compact human-readable label such as
    ///     "Airoli, Navi Mumbai"; <c>null</c> when Nominatim was
    ///     unreachable, timed out, rate-limited, or when the input
    ///     was "unavailable:*" / null.
    ///
    /// Never throws — geocoding failures degrade to <c>Address = null</c>,
    /// which the LocationPin component renders as a bare "View on map"
    /// link. Check-ins must never be blocked by a geocode hiccup.
    /// </summary>
    Task<(string? Coords, string? Address)> LookupAsync(string? raw, CancellationToken ct = default);
}
