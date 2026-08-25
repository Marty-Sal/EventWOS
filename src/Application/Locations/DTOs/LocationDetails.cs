namespace EventOpsOracle.Application.Locations.DTOs;

/// <summary>
/// Structured address for a single point, returned by
/// <see cref="ILocationService.ReverseGeocodeAsync"/> after the admin drags
/// the map marker.
///
/// Every component except the coordinates is nullable on purpose: providers
/// routinely omit City/PostalCode for rural or unnamed points, and a partial
/// answer is still useful for pre-filling the venue form. The caller decides
/// what is mandatory — the Venue aggregate already enforces its own required
/// fields, so this DTO never pretends the provider guarantees them.
/// </summary>
public sealed record LocationDetails(
    string? PlaceId,
    string? Name,
    string? Address,
    string? City,
    string? State,
    string? PostalCode,
    string? Country,
    decimal Latitude,
    decimal Longitude
);
