using EventWOS.Domain.Common;

namespace EventWOS.Domain.Entities;

/// <summary>Join entity: Role ↔ Permission with grant/deny override.</summary>
public sealed class RolePermission : BaseEntity
{
    private RolePermission() { }

    public RolePermission(Guid roleId, Guid permissionId, bool isGranted = true)
    {
        RoleId = roleId;
        PermissionId = permissionId;
        IsGranted = isGranted;
    }

    public Guid RoleId { get; private set; }
    public Guid PermissionId { get; private set; }
    public bool IsGranted { get; private set; } = true;

    // Navigation
    public Role Role { get; private set; } = default!;
    public Permission Permission { get; private set; } = default!;
}
