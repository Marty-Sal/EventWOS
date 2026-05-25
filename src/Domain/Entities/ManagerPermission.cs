using EventWOS.Domain.Common;

namespace EventWOS.Domain.Entities;

/// <summary>
/// Specific permissions dynamically granted to Managers by an Admin.
/// Managers have no default permissions — they must be explicitly assigned.
/// </summary>
public sealed class ManagerPermission : BaseEntity
{
    private ManagerPermission() { }

    public ManagerPermission(Guid managerId, Guid permissionId, Guid grantedByAdminId)
    {
        ManagerId = managerId;
        PermissionId = permissionId;
        GrantedByAdminId = grantedByAdminId;
        IsActive = true;
    }

    public Guid ManagerId { get; private set; }
    public Guid PermissionId { get; private set; }
    public Guid GrantedByAdminId { get; private set; }
    public bool IsActive { get; private set; }
    public DateTime? ExpiresAt { get; private set; }

    // Navigation
    public User Manager { get; private set; } = default!;
    public Permission Permission { get; private set; } = default!;

    public void Revoke()     => IsActive = false;
    public void Reactivate()  => IsActive = true;
    public void SetExpiry(DateTime expiresAt) => ExpiresAt = expiresAt;
}
