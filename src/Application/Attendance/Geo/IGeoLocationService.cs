namespace EventWOS.Application.Attendance.Geo;

/// <summary>
/// Reverse-geocodes a "lat,lng" coordinate into a compact, human-
/// readable address label (e.g. "Airoli, Maharashtra, India"), and
/// returns the enriched storage string in the format
///     "lat,lng|Short Address"
/// that the frontend's LocationPin component knows how to parse.
///
/// The client sends raw "lat,lng"; the API enriches it before writing
/// to AttendanceRecord.Location. No external network calls — the
/// lookup is served from an embedded GeoNames cities15000 dataset
/// (~34k cities globally) via an in-memory KD-tree.
/// </summary>
public interface IGeoLocationService
{
    /// <summary>
    /// If <paramref name="raw"/> is a "lat,lng" string with valid
    /// coordinates, returns "lat,lng|Nearest City, State, Country".
    /// If it already contains an address (has "|"), returns it
    /// verbatim (idempotent). If it's null/empty/"unavailable:*" or
    /// unparseable, returns it verbatim so the caller doesn't have
    /// to special-case.
    /// </summary>
    string Enrich(string? raw);
}
