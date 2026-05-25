using EventWOS.Application.Events.Commands;
using EventWOS.Application.Events.DTOs;
using EventWOS.Application.Interfaces;
using EventWOS.Domain.Enums;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Events.Queries;

public sealed record GetEventByIdQuery(Guid Id) : IRequest<Result<EventDto>>;

public sealed class GetEventByIdHandler : IRequestHandler<GetEventByIdQuery, Result<EventDto>>
{
    private readonly IAppDbContext _db;
    public GetEventByIdHandler(IAppDbContext db) => _db = db;

    public async Task<Result<EventDto>> Handle(GetEventByIdQuery req, CancellationToken ct)
    {
        var ev = await _db.Events
            .Include(e => e.Creator)
            .FirstOrDefaultAsync(e => e.Id == req.Id, ct);

        if (ev is null)
            return Result.Failure<EventDto>(new Error("Event.NotFound", "Event not found."));

        var assignedCrew = await _db.EventAssignments
            .CountAsync(a => a.EventId == req.Id && a.Status != AssignmentStatus.Declined, ct);

        return Result.Success(CreateEventHandler.MapToDto(ev, assignedCrew, ev.Creator.FullName));
    }
}
