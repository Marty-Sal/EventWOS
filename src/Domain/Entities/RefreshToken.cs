using EventWOS.Domain.Common;

namespace EventWOS.Domain.Entities;

/// <summary>
/// Refresh token with rotation support. Each refresh issues a new token
/// and invalidates the previous one (prevents replay attacks).
/// Stored as SHA256 hash — never plaintext.
/// </summary>
public sealed class RefreshToken : BaseEntity
{
    private RefreshToken() { }

    public RefreshToken(Guid userId, string tokenHash, string deviceId, string ipAddress, DateTime expiresAt)
    {
        UserId = userId;
        TokenHash = tokenHash;
        DeviceId = deviceId;
        IpAddress = ipAddress;
        ExpiresAt = expiresAt;
        IsRevoked = false;
    }

    public Guid UserId { get; private set; }
    public string TokenHash { get; private set; } = default!;
    public string DeviceId { get; private set; } = default!;
    public string IpAddress { get; private set; } = default!;
    public DateTime ExpiresAt { get; private set; }
    public bool IsRevoked { get; private set; }
    public DateTime? RevokedAt { get; private set; }
    public string? ReplacedByTokenHash { get; private set; }  // Token rotation chain
    public string? RevokeReason { get; private set; }

    // Navigation
    public User User { get; private set; } = default!;

    public bool IsActive => !IsRevoked && DateTime.UtcNow < ExpiresAt;

    public void Revoke(string reason, string? replacedBy = null)
    {
        IsRevoked = true;
        RevokedAt = DateTime.UtcNow;
        RevokeReason = reason;
        ReplacedByTokenHash = replacedBy;
    }
}
