using EventWOS.Application.Interfaces;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Events.Commands;

public sealed record UpdateEventCommand(
    Guid     Id,
    string   Title,
    string?  Description,
    string   Venue,
    string?  Address,
    DateTime StartAt,
    DateTime EndAt,
    int      MaxCrew
) : IRequest<Result>;

public sealed class UpdateEventHandler : IRequestHandler<UpdateEventCommand, Result>
{
    private readonly IAppDbContext _db;
    private readonly IUnitOfWork   _uow;
    public UpdateEventHandler(IAppDbContext db, IUnitOfWork uow) { _db = db; _uow = uow; }

    public async Task<Result> Handle(UpdateEventCommand req, CancellationToken ct)
    {
        var ev = await _db.Events.FirstOrDefaultAsync(e => e.Id == req.Id, ct);
        if (ev is null) return Result.Failure(new Error("Event.NotFound", "Event not found."));

        ev.Update(req.Title, req.Description, req.Venue, req.Address,
                  req.StartAt, req.EndAt, req.MaxCrew);
        await _uow.SaveChangesAsync(ct);
        return Result.Success();
    }
}
