using EventWOS.Shared.Result;
using MediatR;

namespace EventWOS.Application.Auth.Commands;

/// <summary>
/// First-login password setup for grandfathered users (RequirePasswordReset = true).
/// Functionally identical to ResetPassword but the audit trail records
/// it as a setup rather than a reset, and we allow the user to choose
/// a fresh username at the same time (their backfilled username =
/// their mobile, which most people will want to change).
/// </summary>
public sealed record SetupPasswordCommand(
    Guid    OtpRequestId,
    string  Mobile,
    string  Otp,
    string  NewUsername,
    string  NewPassword
) : IRequest<Result>;
