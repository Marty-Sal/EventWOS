using EventWOS.Application.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Auth.Internal;

/// <summary>
/// Closes out the PREVIOUS login for a given (user, device) pair so a fresh
/// login replaces it instead of stacking a second "active session" beside it.
///
/// Why this is needed: logging in again from the same browser never used to
/// touch the earlier UserSession row or its RefreshToken. The old row only ever
/// flipped inactive on an explicit logout or admin revoke, and the old refresh
/// token stayed valid for its full 30-day window -- so the Sessions page showed
/// the same person two, three, four times over, each with a Revoke button, and
/// each stale refresh token remained genuinely usable to mint new access
/// tokens. Superseding on login keeps exactly one live session per device and
/// shrinks the credential blast radius to the session actually in use.
/// </summary>
internal static class SessionSuperseder
{
    /// <summary>
    /// Terminates active sessions and revokes live refresh tokens belonging to
    /// this (user, device), EXCLUDING nothing -- call it BEFORE adding the new
    /// session/token rows for the current login. Caller owns SaveChanges.
    /// </summary>
    public static async Task SupersedeAsync(
        IAppDbContext db, Guid userId, string deviceId, CancellationToken ct)
    {
        // An "unknown" device id is the catch-all fallback for clients that
        // send nothing. Collapsing every such login into one session would let
        // one browser's login kick an unrelated one out, so leave those alone.
        if (string.IsNullOrWhiteSpace(deviceId) || deviceId == "unknown")
            return;

        var priorSessions = await db.UserSessions
            .Where(s => s.UserId == userId && s.DeviceId == deviceId && s.IsActive)
            .ToListAsync(ct);

        foreach (var s in priorSessions)
            s.Terminate("Superseded by a newer login on the same device");

        var now = DateTime.UtcNow;
        var priorTokens = await db.RefreshTokens
            .Where(r => r.UserId == userId && r.DeviceId == deviceId
                     && !r.IsRevoked && r.ExpiresAt > now)
            .ToListAsync(ct);

        foreach (var t in priorTokens)
            t.Revoke("Superseded by a newer login on the same device");
    }
}
