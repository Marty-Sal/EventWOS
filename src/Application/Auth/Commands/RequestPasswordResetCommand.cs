using EventWOS.Shared.Result;
using MediatR;

namespace EventWOS.Application.Auth.Commands;

/// <summary>
/// Step 1 of forgot-password. Caller provides any of username/email/mobile.
/// We resolve to a single User and send an OTP to their registered mobile
/// (and email, if available). Always returns success even if no user
/// matches — so we don't leak which accounts exist.
/// </summary>
public sealed record RequestPasswordResetCommand(
    string UsernameEmailOrMobile,
    string? IpAddress
) : IRequest<Result<RequestPasswordResetResponse>>;

public sealed record RequestPasswordResetResponse(
    Guid?  OtpRequestId,        // null when no user matched (so client still gets generic flow)
    string MaskedDestination,   // e.g. "+91*****7890" or "j***@example.com"
    string? DevOtp = null        // Dev-only: surfaced on the UI until SMS is production-ready
);
