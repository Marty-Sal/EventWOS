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
    string? VendorName
);

public sealed record UpdateProfileRequest(
    string FullName,
    string? Email,
    string? AvatarUrl,
    string? InviteMessageTemplate = null
);

public sealed record UpdateUserStatusRequest(UserStatus Status);
