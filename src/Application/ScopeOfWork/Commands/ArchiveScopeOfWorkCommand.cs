using EventWOS.Application.Interfaces;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.ScopeOfWork.Commands;

/// <summary>
/// Soft-archive a scope-of-work row. Idempotent — calling on an already-
/// archived row succeeds silently so the UI doesn't have to special-case.
/// Phase B+ will FK event_shifts.scope_of_work_id here; archiving will
/// remain safe because the FK uses ON DELETE RESTRICT and historical shifts
/// keep referencing the archived row (still visible because we IgnoreQueryFilters
/// when loading by id).
/// </summary>
public sealed record ArchiveScopeOfWorkCommand(
    Guid Id,
    Guid ActingUserId
) : IRequest<Result>;

public sealed class ArchiveScopeOfWorkHandler
    : IRequestHandler<ArchiveScopeOfWorkCommand, Result>
{
    private readonly IAppDbContext _db;
    private readonly IUnitOfWork   _uow;
    public ArchiveScopeOfWorkHandler(IAppDbContext db, IUnitOfWork uow) { _db = db; _uow = uow; }

    public async Task<Result> Handle(ArchiveScopeOfWorkCommand req, CancellationToken ct)
    {
        // IgnoreQueryFilters so we can find the row even if a concurrent
        // archive already happened (idempotency).
        var entity = await _db.ScopesOfWork
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == req.Id, ct);
        if (entity is null)
            return Result.Failure(new Error("ScopeOfWork.NotFound", "Scope of work not found."));

        entity.Archive(req.ActingUserId);
        await _uow.SaveChangesAsync(ct);
        return Result.Success();
    }
}
