namespace EventOpsOracle.BlazorWeb.Services;

/// <summary>Provider-neutral suggestion shape — mirrors the API's LocationSearchResult.</summary>
public sealed record LocationSuggestion(
    string  PlaceId,
    string  Name,
    string  ShortAddress,
    string  FullAddress,
    decimal Latitude,
    decimal Longitude,
    // Structured components, so picking a suggestion fills the whole address
    // block in one round trip. Nullable: the provider genuinely omits these for
    // some places (a remote landmark may have no city or postcode).
    string? City       = null,
    string? State      = null,
    string? PostalCode = null,
    string? Country    = null);

/// <summary>Provider-neutral point detail — mirrors the API's LocationDetails.</summary>
public sealed record LocationDetail(
    string? PlaceId,
    string? Name,
    string? Address,
    string? City,
    string? State,
    string? PostalCode,
    string? Country,
    decimal Latitude,
    decimal Longitude);
