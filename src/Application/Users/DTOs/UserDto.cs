using EventWOS.Domain.Enums;

namespace EventWOS.Application.Users.DTOs;

public sealed record UserDto(
    Guid Id,
    string Mobile,
    string FullName,
    string? Email,
    string? AvatarUrl,
    UserRole Role,
    UserStatus Status,
    Guid? ManagerId,
    DateTime? LastLoginAt,
    DateTime CreatedAt,
    // ── Reputation ────────────────────────────────────────────────────────────
    // Cached averages derived from the ratings table. Whichever pair is relevant
    // depends on Role: Rating/RatingCount for a Vendor, CrewRating/
    // CrewRatingCount for Crew.
    //
    // Null average means NOT YET RATED and must render as "no rating", never as
    // zero stars -- an unrated new vendor and a genuinely terrible one are not
    // the same claim to put next to someone's name in a list.
    //
    // The counts travel with the averages on purpose: "4.8" from one event is a
    // very different signal from "4.8" from thirty, and a list that hides the
    // sample size invites acting on the wrong one.
    decimal? Rating          = null,
    int      RatingCount     = 0,
    decimal? CrewRating      = null,
    int      CrewRatingCount = 0
);

public sealed record UserProfileDto(
    Guid Id,
    string Username,
    string Mobile,
    string FullName,
    string? Email,
    string? AvatarUrl,
    UserRole Role,
    UserStatus Status,
    IReadOnlyList<string> Permissions,
    DateTime? LastLoginAt,
    // Vendor-specific
    string? ReferralCode,
    string? BusinessName,
    decimal? Rating,
    int? RatingCount,
    int? EventsCompleted,
    string? InviteMessageTemplate,
    // Crew-specific
    decimal? DisciplineScore,
    int? EventsAttended,
    decimal? CrewRating,
    int? CrewRatingCount,
    Guid? VendorId,
    string? VendorName,
    // Extended profile
    DateTime? DateOfBirth,
    string? City,
    string? State,
    string? Address,
    string? Bio,
    string? Skills,
    int? ExperienceYears,
    string? ContactPersonName,
    string? GstNumber,
    string? Website,
    // Direct-add invite tracking — drives the "please complete your profile" banner
    bool WasDirectlyAdded,
    bool ProfileCompleted,
    // Live/upcoming breakdown of the caller's own events (Vendor: events they're
    // on; Crew: events they're assigned to). Completed already travels above as
    // EventsCompleted for Vendor -- for Crew it's also populated here now,
    // distinct from EventsAttended (which is personal check-in count, not
    // "the event finished").
    int? EventsLive     = null,
    int? EventsUpcoming = null,

    /// <summary>
    /// The user's current profile photo, as a FileDocument id — null when they
    /// never uploaded one. Deliberately an id and not a URL: profile photos are
    /// private files served through the authenticated /files/{id}/download
    /// endpoint, so there is no public URL to hand out. The legacy AvatarUrl
    /// above is for externally hosted images and is unrelated.
    /// </summary>
    Guid? ProfilePhotoFileId = null
);

public sealed record UpdateProfileRequest(
    string FullName,
    string? Email,
    string? AvatarUrl,
    string? InviteMessageTemplate = null,
    DateTime? DateOfBirth       = null,
    string?   City              = null,
    string?   State             = null,
    string?   Address           = null,
    string?   Bio               = null,
    string?   Skills            = null,
    int?      ExperienceYears   = null,
    string?   BusinessName      = null,
    string?   ContactPersonName = null,
    string?   GstNumber         = null,
    string?   Website           = null
);

public sealed record UpdateUserStatusRequest(UserStatus Status);
