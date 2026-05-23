using EventWOS.Domain.Common;

namespace EventWOS.Domain.Entities;

/// <summary>
/// Granular permission definition following resource:action convention.
/// Examples: users:read, users:write, vendors:manage, crew:invite
/// </summary>
public sealed class Permission : BaseEntity
{
    private Permission() { }

    public Permission(string name, string resource, string action, string? description = null)
    {
        Name = name;
        Resource = resource;
        Action = action;
        Description = description;
    }

    public string Name { get; private set; } = default!;       // e.g. "users:read"
    public string Resource { get; private set; } = default!;   // e.g. "users"
    public string Action { get; private set; } = default!;     // e.g. "read"
    public string? Description { get; private set; }

    // Navigation
    public ICollection<RolePermission> RolePermissions { get; private set; } = new List<RolePermission>();
    public ICollection<UserRolePermission> UserPermissions { get; private set; } = new List<UserRolePermission>();
    public ICollection<ManagerPermission> ManagerPermissions { get; private set; } = new List<ManagerPermission>();
}
