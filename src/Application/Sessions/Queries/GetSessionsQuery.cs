using EventWOS.Application.Interfaces;
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
        var q = _db.UserSessions.AsNoTracking().Where(s => s.IsActive);

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
            .ToListAsync(ct);

        return Result.Success<IReadOnlyList<SessionDto>>(sessions);
    }
}
