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
    string Mobile,
    string FullName,
    string? Email,
    string? AvatarUrl,
    UserRole Role,
    UserStatus Status,
    IReadOnlyList<string> Permissions,
    DateTime? LastLoginAt
);

public sealed record UpdateProfileRequest(
    string FullName,
    string? Email,
    string? AvatarUrl
);

public sealed record UpdateUserStatusRequest(UserStatus Status);
