using EventWOS.Shared.Result;
using MediatR;

namespace EventWOS.Application.Auth.Commands;

/// <summary>Initiates OTP login flow. Returns the OTP request ID for client tracking.</summary>
public sealed record RequestOtpCommand(
    string Mobile,
    string? DeviceId,
    string? IpAddress,
    string? UserAgent
) : IRequest<Result<RequestOtpResponse>>;

public sealed record RequestOtpResponse(
    Guid OtpRequestId,
    string Mobile,
    int ExpiryMinutes,
    string Message,
    string? DevOtp = null   // Only set in development mode
);
