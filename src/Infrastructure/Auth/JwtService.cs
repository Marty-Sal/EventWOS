using EventWOS.Application.Auth.Interfaces;
using EventWOS.Domain.Entities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;

namespace EventWOS.Infrastructure.Auth;

/// <summary>
/// RSA256 JWT service. Uses asymmetric keys for signing — public key
/// can be distributed to other services for validation without sharing secrets.
/// </summary>
public sealed class JwtService : IJwtService
{
    private readonly JwtOptions _options;
    private readonly RSA _privateKey;
    private readonly RSA _publicKey;

    public JwtService(IOptions<JwtOptions> options)
    {
        _options = options.Value;
        _privateKey = RSA.Create();
        _privateKey.ImportRSAPrivateKey(Convert.FromBase64String(_options.PrivateKey), out _);
        _publicKey = RSA.Create();
        _publicKey.ImportRSAPublicKey(Convert.FromBase64String(_options.PublicKey), out _);
    }

    public string GenerateAccessToken(User user, Guid sessionId, IReadOnlyList<string> permissions)
    {
        var signingCredentials = new SigningCredentials(
            new RsaSecurityKey(_privateKey),
            SecurityAlgorithms.RsaSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub,  user.Id.ToString()),
            new(JwtRegisteredClaimNames.Jti,  Guid.NewGuid().ToString()),
            new(JwtRegisteredClaimNames.Iat,  DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()),
            new("mobile",                     user.Mobile),
            new("username",                   user.Username ?? user.Mobile),
            new("role",                       user.Role.ToString()),
            new("session_id",                 sessionId.ToString()),
            new("device_id",                  user.DeviceId ?? string.Empty),
        };

        foreach (var perm in permissions)
            claims.Add(new Claim("permission", perm));

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: DateTime.UtcNow.AddMinutes(_options.AccessTokenExpiryMinutes),
            signingCredentials: signingCredentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public (string Raw, string Hash) GenerateRefreshToken()
    {
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        var hash = ComputeSha256(raw);
        return (raw, hash);
    }

    public ClaimsPrincipalResult? ValidateToken(string token)
    {
        try
        {
            var handler = new JwtSecurityTokenHandler();
            var validationParams = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = _options.Issuer,
                ValidateAudience = true,
                ValidAudience = _options.Audience,
                ValidateLifetime = true,
                IssuerSigningKey = new RsaSecurityKey(_publicKey),
                ValidAlgorithms = new[] { SecurityAlgorithms.RsaSha256 },
                ClockSkew = TimeSpan.Zero
            };

            var principal = handler.ValidateToken(token, validationParams, out _);

            var userId = Guid.Parse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub)!);
            var mobile = principal.FindFirstValue("mobile")!;
            var role = principal.FindFirstValue("role")!;
            var sessionId = Guid.Parse(principal.FindFirstValue("session_id")!);
            var deviceId = principal.FindFirstValue("device_id");
            var permissions = principal.FindAll("permission").Select(c => c.Value).ToList();

            return new ClaimsPrincipalResult(userId, mobile, role, sessionId, deviceId, permissions);
        }
        catch
        {
            return null;
        }
    }

    private static string ComputeSha256(string input)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
    }
}

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";
    public string Issuer { get; init; } = default!;
    public string Audience { get; init; } = default!;
    public string PrivateKey { get; init; } = default!; // Base64 RSA private key
    public string PublicKey { get; init; } = default!;  // Base64 RSA public key
    public int AccessTokenExpiryMinutes { get; init; } = 60;
}
