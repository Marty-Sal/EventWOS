using EventWOS.Application.Interfaces;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.ScopeOfWork.Commands;

/// <summary>
/// Restore (un-archive) a scope-of-work row.
///
/// Edge case: restoring a row whose Name collides with another currently
/// active row would violate the filtered unique index. We surface that as
/// a clean error so the UI can prompt the admin to rename one of them.
/// </summary>
public sealed record RestoreScopeOfWorkCommand(
    Guid Id,
    Guid ActingUserId
) : IRequest<Result>;

public sealed class RestoreScopeOfWorkHandler
    : IRequestHandler<RestoreScopeOfWorkCommand, Result>
{
    private readonly IAppDbContext _db;
    private readonly IUnitOfWork   _uow;
    public RestoreScopeOfWorkHandler(IAppDbContext db, IUnitOfWork uow) { _db = db; _uow = uow; }

    public async Task<Result> Handle(RestoreScopeOfWorkCommand req, CancellationToken ct)
    {
        var entity = await _db.ScopesOfWork
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == req.Id, ct);
        if (entity is null)
            return Result.Failure(new Error("ScopeOfWork.NotFound", "Scope of work not found."));

        if (!entity.IsDeleted) return Result.Success();   // already active

        // Pre-flight collision check on Name. If an active row already has
        // this name, the unique index will reject the restore — but a clean
        // error here is friendlier than the raw DbUpdateException.
        var collision = await _db.ScopesOfWork
            .Where(s => !s.IsDeleted && s.Id != req.Id && s.Name.ToLower() == entity.Name.ToLower())
            .AnyAsync(ct);
        if (collision)
            return Result.Failure(new Error(
                "ScopeOfWork.RestoreCollision",
                $"Another active scope already uses the name \"{entity.Name}\". " +
                "Rename it (or the archived one) before restoring."));

        entity.Restore();
        entity.UpdatedBy = req.ActingUserId;
        await _uow.SaveChangesAsync(ct);
        return Result.Success();
    }
}
