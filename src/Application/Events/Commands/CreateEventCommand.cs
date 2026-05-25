using EventWOS.Application.Events.DTOs;
using EventWOS.Application.Interfaces;
using EventWOS.Domain.Entities;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Result;
using MediatR;

namespace EventWOS.Application.Events.Commands;

public sealed record CreateEventCommand(
    string   Title,
    string?  Description,
    string   Venue,
    string?  Address,
    DateTime StartAt,
    DateTime EndAt,
    int      MaxCrew,
    Guid     CreatedByUserId
) : IRequest<Result<EventDto>>;

public sealed class CreateEventHandler : IRequestHandler<CreateEventCommand, Result<EventDto>>
{
    private readonly IAppDbContext _db;
    private readonly IUnitOfWork   _uow;
    public CreateEventHandler(IAppDbContext db, IUnitOfWork uow) { _db = db; _uow = uow; }

    public async Task<Result<EventDto>> Handle(CreateEventCommand req, CancellationToken ct)
    {
        var ev = new Event(req.Title, req.Description, req.Venue, req.Address,
                           req.StartAt, req.EndAt, req.CreatedByUserId, req.MaxCrew);
        _db.Events.Add(ev);
        await _uow.SaveChangesAsync(ct);

        var creator = await _db.Users.FindAsync(new object[] { req.CreatedByUserId }, ct);
        return Result.Success(MapToDto(ev, 0, creator?.FullName ?? "Unknown"));
    }

    internal static EventDto MapToDto(Domain.Entities.Event ev, int assignedCrew, string creatorName) => new(
        ev.Id, ev.Title, ev.Description, ev.Venue, ev.Address,
        ev.StartAt, ev.EndAt, ev.Status.ToString(), ev.MaxCrew,
        assignedCrew, ev.CreatedByUserId, creatorName, ev.CreatedAt);
}
