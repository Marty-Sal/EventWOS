using EventWOS.Domain.Common;
using EventWOS.Domain.Enums;
using EventWOS.Domain.Events;

namespace EventWOS.Domain.Entities;

/// <summary>
/// Core user aggregate root for the Event Workforce Operating System.
/// Represents any actor in the system: Admin, Manager, Vendor, or Crew.
/// </summary>
public sealed class User : BaseEntity
{
    private User() { } // EF Core

    public User(
        string mobile,
        string fullName,
        UserRole role,
        Guid? managerId = null)
    {
        Mobile = mobile;
        FullName = fullName;
        Role = role;
        ManagerId = managerId;
        Status = UserStatus.Pending;
        AddDomainEvent(new UserCreatedEvent(Id, mobile, role));
    }

    public string Mobile { get; private set; } = default!;
    public string FullName { get; set; } = default!;
    public string? Email { get; set; }
    public string? AvatarUrl { get; set; }
    public UserRole Role { get; private set; }
    public UserStatus Status { get; private set; }
    public Guid? ManagerId { get; private set; }
    public string? DeviceId { get; set; }
    public string? LastKnownIp { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public int FailedOtpAttempts { get; private set; }
    public DateTime? LockedUntil { get; private set; }

    // Navigation
    public User? Manager { get; private set; }
    public ICollection<RefreshToken> RefreshTokens { get; private set; } = new List<RefreshToken>();
    public ICollection<UserSession> Sessions { get; private set; } = new List<UserSession>();
    public ICollection<UserRolePermission> RolePermissions { get; private set; } = new List<UserRolePermission>();
    public ICollection<VendorCrewMapping> VendorMappings { get; private set; } = new List<VendorCrewMapping>();

    /// <summary>Activates user on first successful OTP login.</summary>
    public void Activate()
    {
        Status = UserStatus.Active;
        LastLoginAt = DateTime.UtcNow;
        FailedOtpAttempts = 0;
        LockedUntil = null;
    }

    /// <summary>Records a failed OTP attempt and locks account after 5 failures.</summary>
    public void RecordFailedOtpAttempt()
    {
        FailedOtpAttempts++;
        if (FailedOtpAttempts >= 5)
            LockedUntil = DateTime.UtcNow.AddMinutes(30);
    }

    public void ResetFailedAttempts() => FailedOtpAttempts = 0;

    public bool IsLocked => LockedUntil.HasValue && LockedUntil.Value > DateTime.UtcNow;

    public void ChangeRole(UserRole newRole)
    {
        var oldRole = Role;
        Role = newRole;
        AddDomainEvent(new UserRoleChangedEvent(Id, oldRole, newRole));
    }

    public void Suspend(Guid adminId)
    {
        Status = UserStatus.Suspended;
        AddDomainEvent(new UserStatusChangedEvent(Id, adminId, Status));
    }

    public void Deactivate(Guid adminId)
    {
        Status = UserStatus.Deactivated;
        AddDomainEvent(new UserStatusChangedEvent(Id, adminId, Status));
    }

    public void Reactivate(Guid adminId)
    {
        Status = UserStatus.Active;
        AddDomainEvent(new UserStatusChangedEvent(Id, adminId, Status));
    }

    public void UpdateProfile(string fullName, string? email, string? avatarUrl)
    {
        FullName = fullName;
        Email = email;
        AvatarUrl = avatarUrl;
        UpdatedAt = DateTime.UtcNow;
    }

    public void UpdateLoginMetadata(string ip, string? deviceId)
    {
        LastKnownIp = ip;
        DeviceId = deviceId;
        LastLoginAt = DateTime.UtcNow;
    }
}
