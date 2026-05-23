using EventWOS.Shared.Result;
using MediatR;

namespace EventWOS.Application.Auth.Commands;

/// <summary>Rotates refresh token and issues new access token. Old token is revoked.</summary>
public sealed record RefreshTokenCommand(
    string RefreshToken,
    string? DeviceId,
    string? IpAddress
) : IRequest<Result<AuthResponse>>;
