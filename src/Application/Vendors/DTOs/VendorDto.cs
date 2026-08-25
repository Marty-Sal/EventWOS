using EventOpsOracle.Application.Files.DTOs;

namespace EventOpsOracle.Application.Vendors.DTOs;

public sealed record VendorDto(
    Guid   Id,
    string Mobile,
    string FullName,
    string? BusinessName,
    string? Email,
    string? AvatarUrl,
    string  Status,
    string? ReferralCode,
    /// <summary>Average across this vendor's rated events. Null = not yet rated.</summary>
    decimal? Rating,
    /// <summary>How many rated events back that average.</summary>
    int     RatingCount,
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
    IReadOnlyList<FileDocumentDto>? Files = null,
    // Direct-add invite tracking (null/false for self-registered vendors)
    bool WasDirectlyAdded = false,
    bool ProfileCompleted = false
);

public sealed record VendorListItemDto(
    Guid   Id,
    string Mobile,
    string FullName,
    string? BusinessName,
    string  Status,
    string? ReferralCode,
    decimal? Rating,
    int     RatingCount,
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

/// <summary>
/// Two axes, 1-5 each, plus an optional note. The event is a route value rather
/// than a body field, because a rating with no event attached is exactly what the
/// old model allowed and could never average.
/// </summary>
public sealed record RateVendorRequest(int Performance, int Cooperation, string? Comment = null);

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
    DateTime CreatedAt,
    /// <summary>
    /// Average of this crew member's ratings. Null = not yet rated, which must
    /// render as "no rating" rather than zero stars -- a new hire and a bad one
    /// are not the same claim.
    /// </summary>
    decimal? CrewRating = null,
    /// <summary>Ratings behind the average, so a single review is not mistaken for a track record.</summary>
    int     CrewRatingCount = 0
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
    IReadOnlyList<FileDocumentDto> Files,
    bool WasDirectlyAdded = false,
    bool ProfileCompleted = false,
    decimal? CrewRating = null,
    int      CrewRatingCount = 0
);

public sealed record JoinVendorRequest(string ReferralCode);
