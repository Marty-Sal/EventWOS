namespace EventWOS.Application.Venues.DTOs;

/// <summary>Read-side shape for the Venue catalog.</summary>
public sealed record VenueDto(
    Guid      Id,
    string    Name,
    string    AddressLine1,
    string?   AddressLine2,
    string?   ShortAddress,
    string    City,
    string?   State,
    string?   PostalCode,
    string?   Country,
    double?   Latitude,
    double?   Longitude,
    string?   Notes,
    bool      IsArchived,
    DateTime  CreatedAt,
    DateTime? UpdatedAt
);
