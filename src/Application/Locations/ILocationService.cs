using EventWOS.Application.Locations.DTOs;

namespace EventWOS.Application.Locations;

/// <summary>
/// The ONLY location-provider contract the application layer is allowed to
/// know about. Implementations live in Infrastructure (see
/// NominatimLocationService) and are chosen by configuration, so switching
/// OpenStreetMap → Google Maps → Mappls is a DI/appsettings change with zero
/// edits to handlers, controllers or Blazor components.
///
/// Contract rules every implementation must honour:
///
///  * Never throw for "no results" — return an empty list. An empty result is
///    a normal outcome of a search box, not an error.
///  * Never throw for provider trouble (timeout, 5xx, rate-limit, malformed
///    payload). Return empty / null and log. A geocoding hiccup must never
///    surface as a 500 to the admin, and must never block a save.
///  * DO propagate <see cref="OperationCanceledException"/> when the caller's
///    token is cancelled — that is the debounced-search case where the admin
///    typed another character and we genuinely want to abandon the old call.
///  * Enforce an internal timeout so a hung provider can't pin a request
///    thread; the timeout is configuration, not a magic number.
///
/// Note this is intentionally separate from
/// <see cref="EventWOS.Application.Attendance.Geo.IGeoLocationService"/>,
/// which is a narrow best-effort "turn a crew GPS fix into a display label"
/// helper used when writing AttendanceRecord rows. This interface is the
/// admin-facing search/geocode capability. Keeping them apart stops the
/// attendance write-path from growing a dependency on venue-search concerns.
/// </summary>
public interface ILocationService
{
    /// <summary>
    /// Free-text place lookup for the venue search box.
    /// Returns an empty list for a blank/too-short query, for no matches, and
    /// for any provider failure. Ordering is the provider's relevance order.
    /// </summary>
    Task<IReadOnlyList<LocationSearchResult>> SearchAsync(
        string query, CancellationToken cancellationToken);

    /// <summary>
    /// Resolve a structured address for an exact point — used after the admin
    /// drags the marker. Returns <c>null</c> when the coordinates are out of
    /// range, or when the provider fails or has nothing at that point.
    /// </summary>
    Task<LocationDetails?> ReverseGeocodeAsync(
        decimal latitude, decimal longitude, CancellationToken cancellationToken);
}
