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
public sealed record LocationSearchResult(
    string  PlaceId,
    string  Name,
    string  ShortAddress,
    string  FullAddress,
    decimal Latitude,
    decimal Longitude
);
