using EventWOS.Domain.Common;
using EventWOS.Domain.Enums;

namespace EventWOS.Domain.Entities;

/// <summary>System role definition — maps to UserRole enum but allows runtime metadata.</summary>
public sealed class Role : BaseEntity
{
    private Role() { }

    public Role(string name, string description, UserRole roleType)
    {
        Name = name;
        Description = description;
        RoleType = roleType;
        IsSystem = true;
    }

    public string Name { get; private set; } = default!;
    public string Description { get; private set; } = default!;
    public UserRole RoleType { get; private set; }
    public bool IsSystem { get; private set; } // System roles cannot be deleted

    // Navigation
    public ICollection<RolePermission> Permissions { get; private set; } = new List<RolePermission>();
}
