using EventOpsOracle.Domain.Common;

namespace EventOpsOracle.Domain.Entities;

/// <summary>
/// Tracks active user sessions. Supports multi-device, session revocation,
/// and device fingerprinting for security.
/// </summary>
public sealed class UserSession : BaseEntity
{
    private UserSession() { }

    public UserSession(
        Guid userId,
        Guid sessionId,
        string deviceId,
        string deviceName,
        string ipAddress,
        string userAgent)
    {
        UserId = userId;
        SessionId = sessionId;
        DeviceId = deviceId;
        DeviceName = deviceName;
        IpAddress = ipAddress;
        UserAgent = userAgent;
        LastActivityAt = DateTime.UtcNow;
        IsActive = true;
    }

    public Guid UserId { get; private set; }
    public Guid SessionId { get; private set; }
    public string DeviceId { get; private set; } = default!;
    public string DeviceName { get; private set; } = default!;
    public string IpAddress { get; private set; } = default!;
    public string UserAgent { get; private set; } = default!;
    public DateTime LastActivityAt { get; private set; }
    public bool IsActive { get; private set; }
    public DateTime? TerminatedAt { get; private set; }
    public string? TerminationReason { get; private set; }

    // Navigation
    public User User { get; private set; } = default!;

    public void UpdateActivity() => LastActivityAt = DateTime.UtcNow;

    /// <summary>
    /// Points this session row at a NEW JWT session_id, following a token
    /// refresh. Every access token carries a session_id claim, and the auth
    /// middleware rejects any token whose claim has no matching active row --
    /// so if this is not called on rotation, the very next request after a
    /// refresh is rejected as "revoked" even though the user never logged out.
    /// That silent break was the actual cause of session pileup: the client
    /// reacted to the bogus 401 by clearing local storage and forcing a fresh
    /// login, abandoning this row instead of ending it.
    /// </summary>
    public void RotateSessionId(Guid newSessionId)
    {
        SessionId = newSessionId;
        LastActivityAt = DateTime.UtcNow;
    }

    public void Terminate(string reason)
    {
        IsActive = false;
        TerminatedAt = DateTime.UtcNow;
        TerminationReason = reason;
    }
}
