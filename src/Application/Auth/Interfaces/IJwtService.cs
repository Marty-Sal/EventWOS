using EventWOS.Domain.Entities;

namespace EventWOS.Application.Auth.Interfaces;

/// <summary>JWT token generation and validation abstraction.</summary>
public interface IJwtService
{
    /// <summary>Generates a signed RSA256 JWT access token.</summary>
    string GenerateAccessToken(User user, Guid sessionId, IReadOnlyList<string> permissions);

    /// <summary>Generates a cryptographically random refresh token (raw value). Returns (raw, hash).</summary>
    (string Raw, string Hash) GenerateRefreshToken();

    /// <summary>Validates a JWT token and returns claims. Returns null if invalid.</summary>
    ClaimsPrincipalResult? ValidateToken(string token);
}

public sealed record ClaimsPrincipalResult(
    Guid UserId,
    string Mobile,
    string Role,
    Guid SessionId,
    string? DeviceId,
    IReadOnlyList<string> Permissions
);
