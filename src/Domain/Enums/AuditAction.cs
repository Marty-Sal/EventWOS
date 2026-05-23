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
    PasswordChanged
}
