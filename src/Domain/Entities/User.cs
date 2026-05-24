using EventWOS.Domain.Common;
using EventWOS.Domain.Enums;
using EventWOS.Domain.Events;

namespace EventWOS.Domain.Entities;

/// <summary>
/// Core user aggregate root. Represents Admin, Manager, Vendor, or Crew.
/// Vendor-specific: BusinessName, ReferralCode, Rating
/// Crew-specific: DisciplineScore, VendorId (which vendor they belong to)
/// </summary>
public sealed class User : BaseEntity
{
    private User() { }

    public User(
        string mobile,
        string fullName,
        UserRole role,
        Guid? managerId = null)
    {
        Mobile   = mobile;
        FullName = fullName;
        Role     = role;
        ManagerId = managerId;
        Status   = UserStatus.Pending;

        // Vendor: auto-generate referral code
        if (role == UserRole.Vendor)
            ReferralCode = GenerateReferralCode();

        AddDomainEvent(new UserCreatedEvent(Id, mobile, role));
    }

    // ── Core ──────────────────────────────────────────────────────────────────
    public string  Mobile           { get; private set; } = default!;
    public string  FullName         { get; set; }         = default!;
    public string? Email            { get; set; }
    public string? AvatarUrl        { get; set; }
    public UserRole    Role         { get; private set; }
    public UserStatus  Status       { get; private set; }
    public Guid?   ManagerId        { get; private set; }
    public string? DeviceId         { get; set; }
    public string? LastKnownIp      { get; set; }
    public DateTime? LastLoginAt    { get; set; }
    public int  FailedOtpAttempts   { get; private set; }
    public DateTime? LockedUntil    { get; private set; }

    // ── Vendor-specific ───────────────────────────────────────────────────────
    /// <summary>Business / company name for Vendor accounts.</summary>
    public string? BusinessName     { get; set; }
    /// <summary>Unique code Crew uses to join this Vendor.</summary>
    public string? ReferralCode     { get; private set; }
    /// <summary>Admin-rated vendor score (0.0 – 5.0).</summary>
    public decimal? Rating          { get; private set; }
    /// <summary>Total events completed as a Vendor.</summary>
    public int EventsCompleted      { get; private set; }

    // ── Crew-specific ─────────────────────────────────────────────────────────
    /// <summary>FK to the Vendor this Crew member belongs to.</summary>
    public Guid? VendorId           { get; private set; }
    /// <summary>Discipline score 0–100 (auto-updated by attendance records).</summary>
    public decimal DisciplineScore  { get; private set; } = 100m;
    /// <summary>Total events attended as Crew.</summary>
    public int EventsAttended       { get; private set; }

    // ── Navigation ───────────────────────────────────────────────────────────
    public User? Manager { get; private set; }
    public User? Vendor  { get; private set; }    // for Crew → Vendor link
    public ICollection<RefreshToken>       RefreshTokens   { get; private set; } = new List<RefreshToken>();
    public ICollection<UserSession>        Sessions        { get; private set; } = new List<UserSession>();
    public ICollection<UserRolePermission> RolePermissions { get; private set; } = new List<UserRolePermission>();
    public ICollection<VendorCrewMapping>  VendorMappings  { get; private set; } = new List<VendorCrewMapping>();

    // ── Behaviours ───────────────────────────────────────────────────────────
    public void Activate()
    {
        Status             = UserStatus.Active;
        LastLoginAt        = DateTime.UtcNow;
        FailedOtpAttempts  = 0;
        LockedUntil        = null;
    }

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
        var old = Role;
        Role = newRole;
        if (newRole == UserRole.Vendor && ReferralCode is null)
            ReferralCode = GenerateReferralCode();
        AddDomainEvent(new UserRoleChangedEvent(Id, old, newRole));
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
        FullName   = fullName;
        Email      = email;
        AvatarUrl  = avatarUrl;
        UpdatedAt  = DateTime.UtcNow;
    }

    public void UpdateLoginMetadata(string ip, string? deviceId)
    {
        LastKnownIp = ip;
        DeviceId    = deviceId;
        LastLoginAt = DateTime.UtcNow;
    }

    /// <summary>Admin rates a Vendor (0.0–5.0).</summary>
    public void SetRating(decimal rating)
    {
        if (Role != UserRole.Vendor) throw new InvalidOperationException("Only Vendors can be rated.");
        Rating = Math.Clamp(rating, 0m, 5m);
    }

    /// <summary>Crew joins a Vendor (called after referral code validation).</summary>
    public void JoinVendor(Guid vendorId)
    {
        if (Role != UserRole.Crew) throw new InvalidOperationException("Only Crew can join a Vendor.");
        VendorId = vendorId;
    }

    /// <summary>Updates Crew discipline score (called by attendance service).</summary>
    public void UpdateDisciplineScore(decimal score)
    {
        DisciplineScore = Math.Clamp(score, 0m, 100m);
    }

    public void IncrementEventsAttended() => EventsAttended++;
    public void IncrementEventsCompleted() => EventsCompleted++;

    private static string GenerateReferralCode()
        => Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
}
