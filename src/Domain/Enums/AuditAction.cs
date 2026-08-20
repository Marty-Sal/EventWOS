namespace EventWOS.Domain.Enums;

public enum AuditAction
{
    Login,
    Logout,
    OtpRequested,
    OtpVerified,
    OtpFailed,
    UserCreated,
    UserUpdated,
    UserStatusChanged,
    RoleAssigned,
    PermissionGranted,
    PermissionRevoked,
    SessionRevoked,
    AdminOverride,
    TokenRefreshed,
    PasswordChanged,

    // File & Image Storage module
    FileUploaded,
    FileDeleted,
    /// <summary>Any read of a CrewIdentificationProof file — including the owner viewing their own. Required by policy: sensitive PII access is always logged.</summary>
    SensitiveDocumentAccessed,

    /// <summary>Admin/Manager/Vendor asked a pending applicant (by email) for more information before deciding.</summary>
    UserNotified
}
