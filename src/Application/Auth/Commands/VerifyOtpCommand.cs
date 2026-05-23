using EventWOS.Shared.Result;
using MediatR;

namespace EventWOS.Application.Auth.Commands;

/// <summary>Verifies OTP and issues JWT + refresh token on success.</summary>
public sealed record VerifyOtpCommand(
    string Mobile,
    string Otp,
    Guid OtpRequestId,
    string? DeviceId,
    string? DeviceName,
    string? IpAddress,
    string? UserAgent
) : IRequest<Result<AuthResponse>>;

public sealed record AuthResponse(
    string AccessToken,
    string RefreshToken,
    DateTime AccessTokenExpiry,
    DateTime RefreshTokenExpiry,
    Guid SessionId,
    UserAuthInfo User
);

public sealed record UserAuthInfo(
    Guid Id,
    string Mobile,
    string FullName,
    string Role,
    IReadOnlyList<string> Permissions
);
