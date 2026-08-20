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
    DateTime CreatedAt
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
    string? InviteMessageTemplate,
    // Crew-specific
    decimal? DisciplineScore,
    int? EventsAttended,
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
    bool ProfileCompleted
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
