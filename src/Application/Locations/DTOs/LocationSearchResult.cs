namespace EventWOS.Application.Locations.DTOs;

/// <summary>
/// One autocomplete suggestion returned by <see cref="ILocationService.SearchAsync"/>.
///
/// Deliberately provider-neutral: <see cref="PlaceId"/> is an opaque string
/// (Nominatim hands back a numeric osm id, Google a "place_id", Mappls an
/// "eLoc") so swapping providers never changes this contract. Nothing in the
/// business layer parses or interprets PlaceId — it is echo-back only.
///
/// Coordinates are <see cref="decimal"/> rather than double so the value the
/// provider printed is the value we persist: geographic coordinates are
/// fixed-precision decimal data (6 dp is about 11 cm), and binary floating
/// point cannot represent those exactly. Distance maths converts to double at
/// the point of calculation instead — see GeoDistance.
/// </summary>
/// <remarks>
/// The structured components (City/State/PostalCode/Country) are carried here
/// as well as on <see cref="LocationDetails"/> on purpose. The provider already
/// returns them in the SEARCH response, so picking a suggestion can fill the
/// whole venue form in one round trip. Omitting them meant the admin picked a
/// place, got a name and coordinates, and still had to type the city and state
/// by hand — or drag the pin a pixel to trigger a reverse-geocode that fetched
/// data we'd already been given and thrown away.
/// </remarks>
public sealed record LocationSearchResult(
    string  PlaceId,
    string  Name,
    string  ShortAddress,
    string  FullAddress,
    decimal Latitude,
    decimal Longitude,
    string? City       = null,
    string? State      = null,
    string? PostalCode = null,
    string? Country    = null
);
