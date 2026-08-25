using EventOpsOracle.Application.Events.Commands;
using EventOpsOracle.Application.Events.DTOs;
using EventOpsOracle.Application.Interfaces;
using EventOpsOracle.Domain.Enums;
using EventOpsOracle.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;
using EventOpsOracle.Domain.Rules;

namespace EventOpsOracle.Application.Events.Queries;

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
            .Where(a => a.EventId == req.Id)
            .Where(AssignmentCapacityRules.OccupiesSeat)
            .CountAsync(ct);

        // Phase D step 21: confirmed-only count for the "X/Y crew" UI display.
        var confirmedCrew = await _db.EventAssignments
            .Where(a => a.EventId == req.Id)
            .Where(AssignmentCapacityRules.IsConfirmed)
            .CountAsync(ct);

        return Result.Success(CreateEventHandler.MapToDto(
            ev, assignedCrew, ev.Creator.FullName, confirmedCrew));
    }
}
