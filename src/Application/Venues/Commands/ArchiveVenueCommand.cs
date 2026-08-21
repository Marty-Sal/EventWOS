using EventWOS.Application.Interfaces;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Venues.Commands;

public sealed record ArchiveVenueCommand(Guid Id, Guid ActingUserId) : IRequest<Result>;

public sealed class ArchiveVenueHandler : IRequestHandler<ArchiveVenueCommand, Result>
{
    private readonly IAppDbContext _db;
    private readonly IUnitOfWork   _uow;
    public ArchiveVenueHandler(IAppDbContext db, IUnitOfWork uow) { _db = db; _uow = uow; }

    public async Task<Result> Handle(ArchiveVenueCommand req, CancellationToken ct)
    {
        var entity = await _db.Venues.FirstOrDefaultAsync(v => v.Id == req.Id, ct);
        if (entity is null) return Result.Failure(Error.NotFound);

        entity.Archive(req.ActingUserId);
        await _uow.SaveChangesAsync(ct);
        return Result.Success();
    }
}
