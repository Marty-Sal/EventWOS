using EventWOS.Application.Files.DTOs;

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
    DateTime CreatedAt,
    // Extended profile — same fields shown in the Approval Queue "View
    // details" modal for a pending Vendor registration, now also surfaced
    // for an already-active Vendor via GetVendorByIdQuery so the "View
    // details" modal on the Vendors page shows equivalent depth.
    string? ContactPersonName = null,
    string? GstNumber         = null,
    string? Address           = null,
    string? City              = null,
    string? State             = null,
    string? Website           = null,
    string? Bio               = null,
    DateTime? DateOfBirth     = null,
    IReadOnlyList<FileDocumentDto>? Files = null
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

/// <summary>
/// Full profile for the Crew page's "View details" modal — same shape and
/// same fields as the Approval Queue's PendingRegistrationDto (minus the
/// approval-only fields), so an Admin/Manager/Vendor sees just as much
/// detail for an already-active Crew member as a reviewer sees for a
/// pending one. Returned by GetCrewByIdQuery, not CreateCrewCommand.
/// </summary>
public sealed record CrewDetailDto(
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
    DateTime CreatedAt,
    string? City,
    string? State,
    string? Bio,
    string? Skills,
    int?    ExperienceYears,
    string? ReferralCodeUsed,
    DateTime? DateOfBirth,
    IReadOnlyList<FileDocumentDto> Files
);

public sealed record JoinVendorRequest(string ReferralCode);
