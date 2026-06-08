using EventWOS.Shared.Result;
using MediatR;

namespace EventWOS.Application.Auth.Commands;

/// <summary>
/// Step 2 of forgot-password. Verifies the OTP issued by
/// RequestPasswordResetCommand and replaces the user's password hash.
/// Does NOT auto-login — caller is expected to redirect to the login
/// page with a success toast. (Auto-login mid-reset risks chained CSRF.)
/// </summary>
public sealed record ResetPasswordCommand(
    Guid    OtpRequestId,
    string  Mobile,
    string  Otp,
    string  NewPassword
) : IRequest<Result>;
