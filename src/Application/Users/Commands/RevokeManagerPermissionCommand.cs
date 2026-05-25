using EventWOS.Application.Interfaces;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Users.Commands;

public sealed record RevokeManagerPermissionCommand(
    Guid ManagerId,
    Guid GrantId
) : IRequest<Result>;

public sealed class RevokeManagerPermissionHandler
    : IRequestHandler<RevokeManagerPermissionCommand, Result>
{
    private readonly IAppDbContext _db;
    public RevokeManagerPermissionHandler(IAppDbContext db) => _db = db;

    public async Task<Result> Handle(RevokeManagerPermissionCommand req, CancellationToken ct)
    {
        var grant = await _db.ManagerPermissions.FirstOrDefaultAsync(
            mp => mp.Id == req.GrantId && mp.ManagerId == req.ManagerId, ct);
        if (grant is null)
            return Result.Failure(new Error("Manager.GrantNotFound", "Permission grant not found."));

        grant.Revoke();
        await _db.SaveChangesAsync(ct);
        return Result.Success();
    }
}
