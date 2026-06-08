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

    // ── Auth (password-based login) ──────────────────────────────────────────
    /// <summary>Lowercase-normalized unique handle. Backfilled from Mobile for legacy users.</summary>
    public string?  Username             { get; private set; }
    /// <summary>BCrypt hash. Null while user is awaiting first password setup.</summary>
    public string?  PasswordHash         { get; private set; }
    /// <summary>True when user must complete the OTP-driven password-setup flow before logging in. Set for grandfathered users + restored-from-rejection accounts.</summary>
    public bool     RequirePasswordReset { get; private set; }
    public int      FailedLoginAttempts  { get; private set; }
    public DateTime? LastPasswordChangeAt { get; private set; }

    // ── Rejection (self-registration) ────────────────────────────────────────
    /// <summary>When the registration was rejected. Used to enforce a 24h re-registration cool-down.</summary>
    public DateTime? RejectedAt        { get; private set; }
    public string?   RejectionReason   { get; private set; }
    public Guid?     RejectedByUserId  { get; private set; }
    /// <summary>Optional — admin notes captured at approval time, surfaced in audit trail.</summary>
    public DateTime? ApprovedAt        { get; private set; }
    public Guid?     ApprovedByUserId  { get; private set; }

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
    public decimal? CrewRating        { get; private set; }  // Average rating by Vendors (0–5)
    public int      CrewRatingCount    { get; private set; }
    /// <summary>Total events attended as Crew.</summary>
    public int EventsAttended       { get; private set; }

    // ── Extended profile (optional, used by self-registered Vendors & Crew) ─
    public string? ContactPersonName { get; set; }
    public string? GstNumber         { get; set; }
    public string? Address           { get; set; }
    public string? City              { get; set; }
    public string? State             { get; set; }
    public string? Website           { get; set; }
    public string? Bio               { get; set; }
    /// <summary>CSV list of skills for Crew (e.g. "stage,lighting,security").</summary>
    public string? Skills            { get; set; }
    public int?    ExperienceYears   { get; set; }
    /// <summary>For self-registered Crew — the referral code typed at registration. Kept for audit.</summary>
    public string? ReferralCodeUsed  { get; private set; }

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

    /// <summary>
    /// Called when a Vendor rates this crew member (0–5 stars).
    /// Uses a rolling average: ((existing * count) + new) / (count + 1).
    /// </summary>
    public void AddCrewRating(decimal rating)
    {
        rating = Math.Clamp(rating, 0m, 5m);
        var total = (CrewRating ?? 0m) * CrewRatingCount + rating;
        CrewRatingCount++;
        CrewRating = Math.Round(total / CrewRatingCount, 2);
    }
    public void IncrementEventsCompleted() => EventsCompleted++;

    // ── Self-registration & password-based auth ─────────────────────────────

    /// <summary>
    /// Factory for self-registered Vendors. Status starts as Pending and the
    /// account cannot log in until an Admin/Manager calls Approve(). Username
    /// is normalized to lowercase to match the unique index. Password hash
    /// stored only — never the raw password.
    /// </summary>
    public static User SelfRegisterVendor(
        string username, string mobile, string email, string fullName,
        string passwordHash, string businessName,
        string? contactPersonName, string? gstNumber, string? address,
        string? city, string? state, string? website, string? bio)
    {
        var u = new User(mobile, fullName, UserRole.Vendor)
        {
            Email             = email,
            BusinessName      = businessName,
            ContactPersonName = contactPersonName,
            GstNumber         = gstNumber,
            Address           = address,
            City              = city,
            State             = state,
            Website           = website,
            Bio               = bio
        };
        u.Username             = username.Trim().ToLowerInvariant();
        u.PasswordHash         = passwordHash;
        u.LastPasswordChangeAt = DateTime.UtcNow;
        // Stays Pending until approved.
        return u;
    }

    /// <summary>
    /// Factory for self-registered Crew. ReferralCodeUsed is the literal code
    /// they typed; the resolved VendorId is set via JoinVendor() before save.
    /// </summary>
    public static User SelfRegisterCrew(
        string username, string mobile, string email, string fullName,
        string passwordHash, string? referralCodeUsed,
        string? city, string? skills, int? experienceYears, string? bio)
    {
        var u = new User(mobile, fullName, UserRole.Crew)
        {
            Email           = email,
            City            = city,
            Skills          = skills,
            ExperienceYears = experienceYears,
            Bio             = bio
        };
        u.Username             = username.Trim().ToLowerInvariant();
        u.PasswordHash         = passwordHash;
        u.LastPasswordChangeAt = DateTime.UtcNow;
        u.ReferralCodeUsed     = referralCodeUsed?.Trim().ToUpperInvariant();
        return u;
    }

    /// <summary>Admin/Manager approves a pending self-registration.</summary>
    public void Approve(Guid approverUserId)
    {
        if (Status != UserStatus.Pending)
            throw new InvalidOperationException($"Cannot approve from status {Status}. Account must be Pending.");
        Status            = UserStatus.Active;
        ApprovedAt        = DateTime.UtcNow;
        ApprovedByUserId  = approverUserId;
        FailedOtpAttempts = 0;
        FailedLoginAttempts = 0;
        // Vendor: ensure they have a referral code so Crew can self-register under them.
        if (Role == UserRole.Vendor && ReferralCode is null)
            ReferralCode = GenerateReferralCode();
        AddDomainEvent(new UserStatusChangedEvent(Id, approverUserId, Status));
    }

    /// <summary>Admin/Manager rejects a pending self-registration. Blocks re-registration with same phone/email for 24h.</summary>
    public void Reject(Guid rejectedByUserId, string reason)
    {
        if (Status != UserStatus.Pending)
            throw new InvalidOperationException($"Cannot reject from status {Status}. Account must be Pending.");
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("Rejection reason is required.", nameof(reason));
        Status            = UserStatus.Rejected;
        RejectedAt        = DateTime.UtcNow;
        RejectionReason   = reason.Trim();
        RejectedByUserId  = rejectedByUserId;
        AddDomainEvent(new UserStatusChangedEvent(Id, rejectedByUserId, Status));
    }

    /// <summary>
    /// Sets / changes the password. Hash is pre-computed by IPasswordHasher
    /// in the application layer — domain never touches plaintext.
    /// Clears the RequirePasswordReset flag and any login lockout.
    /// </summary>
    public void SetPassword(string newPasswordHash)
    {
        if (string.IsNullOrWhiteSpace(newPasswordHash))
            throw new ArgumentException("Password hash must be provided.", nameof(newPasswordHash));
        PasswordHash         = newPasswordHash;
        RequirePasswordReset = false;
        LastPasswordChangeAt = DateTime.UtcNow;
        FailedLoginAttempts  = 0;
        LockedUntil          = null;
    }

    /// <summary>Sets/changes the unique username. Always lowercased.</summary>
    public void SetUsername(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("Username is required.", nameof(username));
        Username = username.Trim().ToLowerInvariant();
    }

    /// <summary>Marks a grandfathered user as needing to set a password before next login.</summary>
    public void RequirePasswordSetup() => RequirePasswordReset = true;

    public void RecordFailedLoginAttempt()
    {
        FailedLoginAttempts++;
        if (FailedLoginAttempts >= 5)
            LockedUntil = DateTime.UtcNow.AddMinutes(30);
    }

    public void ResetLoginAttempts()
    {
        FailedLoginAttempts = 0;
        LockedUntil         = null;
    }

    private static string GenerateReferralCode()
        => Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
}
