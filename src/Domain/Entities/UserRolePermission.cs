using EventWOS.Domain.Common;

namespace EventWOS.Domain.Entities;

/// <summary>
/// User-level permission override. Allows granting or denying specific permissions
/// to individual users beyond what their role provides.
/// </summary>
public sealed class UserRolePermission : BaseEntity
{
    private UserRolePermission() { }

    public UserRolePermission(Guid userId, Guid permissionId, bool isGranted, Guid grantedByAdminId)
    {
        UserId = userId;
        PermissionId = permissionId;
        IsGranted = isGranted;
        GrantedByAdminId = grantedByAdminId;
        ExpiresAt = null;
    }

    public Guid UserId { get; private set; }
    public Guid PermissionId { get; private set; }
    public bool IsGranted { get; private set; }
    public Guid GrantedByAdminId { get; private set; }
    public DateTime? ExpiresAt { get; private set; }
    public string? Reason { get; private set; }

    // Navigation
    public User User { get; private set; } = default!;
    public Permission Permission { get; private set; } = default!;

    public bool IsExpired => ExpiresAt.HasValue && ExpiresAt.Value < DateTime.UtcNow;

    public void SetExpiry(DateTime expiresAt) => ExpiresAt = expiresAt;
    public void SetReason(string reason) => Reason = reason;
}
