using EventWOS.Application.Events.DTOs;
using EventWOS.Application.Interfaces;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Events.Queries;

/// <summary>
/// Returns all events the authenticated crew member is assigned to.
/// Does NOT require events:read — Crew can call this with profile:read.
/// </summary>
public sealed record GetMyEventsQuery(Guid CrewId, int Page = 1, int PageSize = 20)
    : IRequest<Result<PagedEventResult>>;

public sealed class GetMyEventsHandler : IRequestHandler<GetMyEventsQuery, Result<PagedEventResult>>
{
    private readonly IAppDbContext _db;
    public GetMyEventsHandler(IAppDbContext db) => _db = db;

    public async Task<Result<PagedEventResult>> Handle(GetMyEventsQuery req, CancellationToken ct)
    {
        // Distinct event IDs the crew member is assigned to
        var assignedEventIds = await _db.EventAssignments
            .AsNoTracking()
            .Where(a => a.CrewId == req.CrewId)
            .Select(a => a.EventId)
            .Distinct()
            .ToListAsync(ct);

        var total = assignedEventIds.Count;

        var items = await _db.Events
            .AsNoTracking()
            .Where(e => assignedEventIds.Contains(e.Id))
            .OrderByDescending(e => e.StartAt)
            .Skip((req.Page - 1) * req.PageSize)
            .Take(req.PageSize)
            .Select(e => new EventListItemDto(
                e.Id, e.Title, e.Venue,
                e.StartAt, e.EndAt,
                e.Status.ToString(),
                e.MaxCrew,
                _db.EventAssignments.Count(a => a.EventId == e.Id),
                e.CreatedAt))
            .ToListAsync(ct);

        return Result.Success(new PagedEventResult(items, total, req.Page, req.PageSize));
    }
}
