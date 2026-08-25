using EventOpsOracle.Application.Interfaces;
using EventOpsOracle.Domain.Interfaces;
using EventOpsOracle.Domain.Rules;
using EventOpsOracle.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventOpsOracle.Application.Sessions.Commands;

/// <summary>
/// Stamps LastActivityAt on the caller's session -- the write behind the
/// client's 30-second heartbeat ping.
///
/// Before this existed the ping was a pure liveness *check* (it only answered
/// 200 vs 401) and nothing ever refreshed LastActivityAt except a token
/// rotation, which happens roughly hourly. That left the Sessions page unable
/// to tell a browser that is open right now from one closed hours ago, so
/// nothing could be aged off the list.
/// </summary>
public sealed record TouchSessionCommand(Guid SessionId) : IRequest<Result>;

public sealed class TouchSessionHandler : IRequestHandler<TouchSessionCommand, Result>
{
    private readonly IAppDbContext _db;
    private readonly IUnitOfWork   _uow;

    public TouchSessionHandler(IAppDbContext db, IUnitOfWork uow)
    {
        _db  = db;
        _uow = uow;
    }

    public async Task<Result> Handle(TouchSessionCommand req, CancellationToken ct)
    {
        var session = await _db.UserSessions
            .FirstOrDefaultAsync(s => s.SessionId == req.SessionId && s.IsActive, ct);

        // No row (or already revoked): nothing to stamp. The auth middleware
        // owns rejecting the request itself -- this is a best-effort touch and
        // must never turn a heartbeat into an error the client reacts to.
        if (session is null) return Result.Success();

        if (DateTime.UtcNow - session.LastActivityAt < SessionActivityRules.HeartbeatWriteFloor)
            return Result.Success();

        session.UpdateActivity();
        await _uow.SaveChangesAsync(ct);
        return Result.Success();
    }
}
