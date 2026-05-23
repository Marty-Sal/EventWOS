namespace EventWOS.Shared.Result;

/// <summary>Structured error with code and human-readable message.</summary>
public sealed record Error(string Code, string Message)
{
    public static readonly Error None = new(string.Empty, string.Empty);

    // Auth errors
    public static readonly Error InvalidOtp = new("Auth.InvalidOtp", "The OTP provided is invalid or expired.");
    public static readonly Error OtpMaxAttempts = new("Auth.OtpMaxAttempts", "Maximum OTP attempts reached. Please request a new OTP.");
    public static readonly Error OtpExpired = new("Auth.OtpExpired", "The OTP has expired. Please request a new one.");
    public static readonly Error AccountLocked = new("Auth.AccountLocked", "Account is temporarily locked due to multiple failed attempts.");
    public static readonly Error AccountSuspended = new("Auth.AccountSuspended", "Account has been suspended. Contact support.");
    public static readonly Error InvalidRefreshToken = new("Auth.InvalidRefreshToken", "Refresh token is invalid or expired.");
    public static readonly Error SessionNotFound = new("Auth.SessionNotFound", "Session not found or already terminated.");
    public static readonly Error Unauthorized = new("Auth.Unauthorized", "You are not authorized to perform this action.");

    // User errors
    public static readonly Error UserNotFound = new("User.NotFound", "User not found.");
    public static readonly Error UserAlreadyExists = new("User.AlreadyExists", "A user with this mobile number already exists.");
    public static readonly Error InvalidRole = new("User.InvalidRole", "Invalid role specified.");

    // General
    public static readonly Error NotFound = new("General.NotFound", "The requested resource was not found.");
    public static readonly Error Conflict = new("General.Conflict", "A conflict occurred with the current state.");
    public static readonly Error Validation = new("General.Validation", "One or more validation errors occurred.");

    public static Error Custom(string code, string message) => new(code, message);
}
