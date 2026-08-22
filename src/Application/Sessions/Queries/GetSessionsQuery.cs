using EventWOS.Application.Interfaces;
using EventWOS.Domain.Rules;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Sessions.Queries;

public sealed record SessionDto(
    Guid Id,
    Guid SessionId,
    Guid UserId,
    string UserFullName,
    string UserRole,
    string DeviceId,
    string DeviceName,
    string IpAddress,
    DateTime LastActivityAt,
    bool IsActive,
    DateTime CreatedAt
);

/// <summary>
/// Get active sessions. When <paramref name="AdminMode"/> is true, returns ALL active sessions
/// across the platform with the owning user's name and role — used by the admin Sessions page.
/// Otherwise returns only the requesting user's own sessions (My Sessions / Profile).
/// </summary>
public sealed record GetSessionsQuery(Guid UserId, bool AdminMode = false) : IRequest<Result<IReadOnlyList<SessionDto>>>;

public sealed class GetSessionsHandler : IRequestHandler<GetSessionsQuery, Result<IReadOnlyList<SessionDto>>>
{
    private readonly IAppDbContext _db;

    public GetSessionsHandler(IAppDbContext db) => _db = db;

    public async Task<Result<IReadOnlyList<SessionDto>>> Handle(GetSessionsQuery request, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        // A session row's own IsActive flag is not enough on its own -- it only
        // flips to false on an EXPLICIT logout or admin revoke. A token that
        // simply expired, or a device whose refresh token already ran out its
        // 30-day window, never triggers either of those, so the row would sit
        // here marked "active" forever with a Revoke button that has nothing
        // left underneath it to revoke.
        //
        // The thing that actually determines whether someone can still get back
        // in on this device is whether a non-revoked, non-expired RefreshToken
        // still exists for that (user, device) pair -- so require one to exist
        // before a session counts as "logged in" for this list.
        var liveDeviceKeys = _db.RefreshTokens
            .AsNoTracking()
            .Where(r => !r.IsRevoked && r.ExpiresAt > now)
            .Select(r => new { r.UserId, r.DeviceId });

        // Second gate: a live refresh token proves the device COULD get back in,
        // not that anyone is actually there. Every signed-in client pings
        // /sessions/ping every 30s and that stamps LastActivityAt, so require a
        // recent heartbeat too -- otherwise a closed browser keeps its row on
        // this page for the refresh token's full 30-day life, which is exactly
        // how the same admin ended up listed twice and a logged-out vendor kept
        // showing as active.
        var heartbeatCutoff = now - SessionActivityRules.HeartbeatGrace;

        var q = _db.UserSessions.AsNoTracking()
            .Where(s => s.IsActive && s.LastActivityAt > heartbeatCutoff)
            .Join(liveDeviceKeys,
                  s => new { s.UserId, s.DeviceId },
                  k => new { k.UserId, k.DeviceId },
                  (s, k) => s);

        if (!request.AdminMode)
            q = q.Where(s => s.UserId == request.UserId);

        // Join with User so we can show name + role on the admin view.
        var sessions = await q
            .Join(_db.Users.AsNoTracking(),
                  s => s.UserId,
                  u => u.Id,
                  (s, u) => new { s, u })
            .OrderByDescending(x => x.s.LastActivityAt)
            .Select(x => new SessionDto(
                x.s.Id, x.s.SessionId, x.u.Id, x.u.FullName, x.u.Role.ToString(),
                x.s.DeviceId, x.s.DeviceName,
                x.s.IpAddress, x.s.LastActivityAt, x.s.IsActive, x.s.CreatedAt))
            // A user with several refresh tokens for the same device (rotation
            // keeps the latest, but a stale one can briefly overlap) would
            // otherwise duplicate the join match into one row per token.
            .Distinct()
            .ToListAsync(ct);

        return Result.Success<IReadOnlyList<SessionDto>>(sessions);
    }
}
