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

        await _uow.SaveChangesAsync(ct);

        await _audit.LogAsync(AuditAction.UserStatusChanged, "User", user.Id.ToString(),
            oldValues: new { Status = oldStatus.ToString() },
            newValues: new { Status = request.NewStatus.ToString() },
            additionalData: $"ByAdmin:{request.PerformedByAdminId}",
            cancellationToken: ct);

        return Result.Success();
    }
}
