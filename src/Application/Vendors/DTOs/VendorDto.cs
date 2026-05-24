namespace EventWOS.Application.Vendors.DTOs;

public sealed record VendorDto(
    Guid   Id,
    string Mobile,
    string FullName,
    string? BusinessName,
    string? Email,
    string? AvatarUrl,
    string  Status,
    string? ReferralCode,
    decimal? Rating,
    int     EventsCompleted,
    int     CrewCount,
    DateTime CreatedAt
);

public sealed record VendorListItemDto(
    Guid   Id,
    string Mobile,
    string FullName,
    string? BusinessName,
    string  Status,
    string? ReferralCode,
    decimal? Rating,
    int     EventsCompleted,
    int     CrewCount,
    DateTime CreatedAt
);

public sealed record CreateVendorRequest(
    string Mobile,
    string FullName,
    string? BusinessName,
    string? Email
);

public sealed record RateVendorRequest(decimal Rating);

public sealed record CreateCrewRequest(
    string Mobile,
    string FullName,
    string? Email,
    string? ReferralCode   // optional: join a vendor on creation
);

public sealed record CrewDto(
    Guid    Id,
    string  Mobile,
    string  FullName,
    string? Email,
    string? AvatarUrl,
    string  Status,
    Guid?   VendorId,
    string? VendorName,
    decimal DisciplineScore,
    int     EventsAttended,
    DateTime CreatedAt
);

public sealed record JoinVendorRequest(string ReferralCode);
