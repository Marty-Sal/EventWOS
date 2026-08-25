using EventOpsOracle.Application.Interfaces;
using EventOpsOracle.Domain.Interfaces;
using EventOpsOracle.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventOpsOracle.Application.Venues.Commands;

public sealed record RestoreVenueCommand(Guid Id, Guid ActingUserId) : IRequest<Result>;

public sealed class RestoreVenueHandler : IRequestHandler<RestoreVenueCommand, Result>
{
    private readonly IAppDbContext _db;
    private readonly IUnitOfWork   _uow;
    public RestoreVenueHandler(IAppDbContext db, IUnitOfWork uow) { _db = db; _uow = uow; }

    public async Task<Result> Handle(RestoreVenueCommand req, CancellationToken ct)
    {
        var entity = await _db.Venues.IgnoreQueryFilters().FirstOrDefaultAsync(v => v.Id == req.Id, ct);
        if (entity is null) return Result.Failure(Error.NotFound);

        entity.Restore();
        await _uow.SaveChangesAsync(ct);
        return Result.Success();
    }
}
