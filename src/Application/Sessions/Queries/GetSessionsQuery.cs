using EventWOS.Application.Interfaces;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Sessions.Queries;

public sealed record SessionDto(
    Guid Id,
    Guid SessionId,
    string DeviceId,
    string DeviceName,
    string IpAddress,
    DateTime LastActivityAt,
    bool IsActive,
    DateTime CreatedAt
);

public sealed record GetSessionsQuery(Guid UserId) : IRequest<Result<IReadOnlyList<SessionDto>>>;

public sealed class GetSessionsHandler : IRequestHandler<GetSessionsQuery, Result<IReadOnlyList<SessionDto>>>
{
    private readonly IAppDbContext _db;

    public GetSessionsHandler(IAppDbContext db) => _db = db;

    public async Task<Result<IReadOnlyList<SessionDto>>> Handle(GetSessionsQuery request, CancellationToken ct)
    {
        var sessions = await _db.UserSessions
            .AsNoTracking()
            .Where(s => s.UserId == request.UserId && s.IsActive)
            .OrderByDescending(s => s.LastActivityAt)
            .Select(s => new SessionDto(
                s.Id, s.SessionId, s.DeviceId, s.DeviceName,
                s.IpAddress, s.LastActivityAt, s.IsActive, s.CreatedAt))
            .ToListAsync(ct);

        return Result.Success<IReadOnlyList<SessionDto>>(sessions);
    }
}
