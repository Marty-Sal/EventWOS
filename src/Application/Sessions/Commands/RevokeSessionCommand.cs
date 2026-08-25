using EventOpsOracle.Domain.Enums;
using EventOpsOracle.Domain.Interfaces;
using EventOpsOracle.Application.Interfaces;
using EventOpsOracle.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventOpsOracle.Application.Sessions.Commands;

public sealed record RevokeSessionCommand(
    Guid SessionRecordId,
    Guid RequestingUserId,
    bool IsAdminOverride = false
) : IRequest<Result>;

public sealed class RevokeSessionHandler : IRequestHandler<RevokeSessionCommand, Result>
{
    private readonly IAppDbContext _db;
    private readonly IUnitOfWork _uow;
    private readonly IAuditLogger _audit;

    public RevokeSessionHandler(IAppDbContext db, IUnitOfWork uow, IAuditLogger audit)
    {
        _db = db; _uow = uow; _audit = audit;
    }

    public async Task<Result> Handle(RevokeSessionCommand request, CancellationToken ct)
    {
        var session = await _db.UserSessions
            .FirstOrDefaultAsync(s => s.Id == request.SessionRecordId, ct);

        if (session is null)
            return Result.Failure(Error.SessionNotFound);

        // Only the session owner or an admin can revoke
        if (!request.IsAdminOverride && session.UserId != request.RequestingUserId)
            return Result.Failure(Error.Unauthorized);

        session.Terminate(request.IsAdminOverride ? "admin_override" : "user_revoked");

        // Also revoke associated refresh tokens for this device
        var tokens = await _db.RefreshTokens
            .Where(r => r.UserId == session.UserId && r.DeviceId == session.DeviceId && !r.IsRevoked)
            .ToListAsync(ct);

        foreach (var t in tokens)
            t.Revoke("session_revoked");

        await _uow.SaveChangesAsync(ct);

        await _audit.LogAsync(
            AuditAction.SessionRevoked, "UserSession", session.Id.ToString(),
            additionalData: $"ByUser:{request.RequestingUserId},AdminOverride:{request.IsAdminOverride}",
            cancellationToken: ct);

        return Result.Success();
    }
}
