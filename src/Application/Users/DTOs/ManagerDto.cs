namespace EventWOS.Application.Users.DTOs;

public sealed record ManagerDto(
    Guid     Id,
    string   Mobile,
    string   FullName,
    string?  Email,
    string?  AvatarUrl,
    string   Status,
    DateTime? LastLoginAt,
    DateTime CreatedAt,
    IReadOnlyList<ManagerPermissionDto> Permissions
);

public sealed record ManagerPermissionDto(
    Guid     GrantId,
    Guid     PermissionId,
    string   Name,
    string   Resource,
    string   Action,
    string?  Description,
    bool     IsActive,
    DateTime? ExpiresAt,
    DateTime GrantedAt
);

public sealed record PermissionDto(
    Guid    Id,
    string  Name,
    string  Resource,
    string  Action,
    string? Description
);

public sealed record CreateManagerRequest(
    string  Mobile,
    string  FullName,
    string? Email
);

public sealed record GrantPermissionRequest(
    Guid      PermissionId,
    DateTime? ExpiresAt = null
);
