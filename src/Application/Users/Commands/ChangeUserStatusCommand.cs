using EventWOS.Domain.Enums;
using EventWOS.Domain.Interfaces;
using EventWOS.Application.Interfaces;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Users.Commands;

public sealed record ChangeUserStatusCommand(
    Guid TargetUserId,
    UserStatus NewStatus,
    Guid PerformedByAdminId
) : IRequest<Result>;

public sealed class ChangeUserStatusHandler : IRequestHandler<ChangeUserStatusCommand, Result>
{
    private readonly IAppDbContext _db;
    private readonly IUnitOfWork _uow;
    private readonly IAuditLogger _audit;

    public ChangeUserStatusHandler(IAppDbContext db, IUnitOfWork uow, IAuditLogger audit)
    {
        _db = db; _uow = uow; _audit = audit;
    }

    public async Task<Result> Handle(ChangeUserStatusCommand request, CancellationToken ct)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == request.TargetUserId && !u.IsDeleted, ct);
        if (user is null) return Result.Failure(Error.UserNotFound);

        var oldStatus = user.Status;

        switch (request.NewStatus)
        {
            case UserStatus.Active:      user.Reactivate(request.PerformedByAdminId); break;
            case UserStatus.Suspended:   user.Suspend(request.PerformedByAdminId); break;
            case UserStatus.Deactivated: user.Deactivate(request.PerformedByAdminId); break;
            default: return Result.Failure(Error.Custom("User.InvalidStatus", "Invalid status transition."));
        }

        // ── Immediate logout for Suspend / Deactivate ────────────────────────────
        // Terminate every active session and revoke every outstanding refresh
        // token for this user. Combined with the JWT OnTokenValidated handler
        // (which checks UserSession.IsActive on every request) and the 30s
        // browser heartbeat, the user is bounced to the login page within
        // ~30 seconds of the status flip — they cannot keep working with a
        // stale token.
        var deactivating = request.NewStatus is UserStatus.Suspended or UserStatus.Deactivated;
        if (deactivating)
        {
            var reason = request.NewStatus == UserStatus.Suspended ? "user_suspended" : "user_deactivated";

            var sessions = await _db.UserSessions
                .Where(s => s.UserId == user.Id && s.IsActive)
                .ToListAsync(ct);
            foreach (var s in sessions)
                s.Terminate(reason);

            var tokens = await _db.RefreshTokens
                .Where(r => r.UserId == user.Id && !r.IsRevoked)
                .ToListAsync(ct);
            foreach (var t in tokens)
                t.Revoke(reason);
        }

        await _uow.SaveChangesAsync(ct);

        await _audit.LogAsync(AuditAction.UserStatusChanged, "User", user.Id.ToString(),
            oldValues: new { Status = oldStatus.ToString() },
            newValues: new { Status = request.NewStatus.ToString() },
            additionalData: $"ByAdmin:{request.PerformedByAdminId}",
            cancellationToken: ct);

        return Result.Success();
    }
}
