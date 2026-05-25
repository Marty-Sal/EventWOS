using EventWOS.Application.Events.DTOs;
using EventWOS.Application.Interfaces;
using EventWOS.Domain.Enums;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Events.Queries;

public sealed record GetEventsQuery(
    int Page = 1, int PageSize = 20,
    string? Search = null,
    EventStatus? Status = null,
    DateTime? From = null,
    DateTime? To = null
) : IRequest<Result<PagedEventResult>>;

public sealed class GetEventsHandler : IRequestHandler<GetEventsQuery, Result<PagedEventResult>>
{
    private readonly IAppDbContext _db;
    public GetEventsHandler(IAppDbContext db) => _db = db;

    public async Task<Result<PagedEventResult>> Handle(GetEventsQuery req, CancellationToken ct)
    {
        var query = _db.Events.AsQueryable();

        if (!string.IsNullOrWhiteSpace(req.Search))
            query = query.Where(e => e.Title.Contains(req.Search) || e.Venue.Contains(req.Search));
        if (req.Status.HasValue)
            query = query.Where(e => e.Status == req.Status.Value);
        if (req.From.HasValue)
            query = query.Where(e => e.StartAt >= req.From.Value);
        if (req.To.HasValue)
            query = query.Where(e => e.StartAt <= req.To.Value);

        var total = await query.CountAsync(ct);

        var events = await query
            .OrderByDescending(e => e.StartAt)
            .Skip((req.Page - 1) * req.PageSize)
            .Take(req.PageSize)
            .ToListAsync(ct);

        var eventIds = events.Select(e => e.Id).ToList();
        var crewCounts = await _db.EventAssignments
            .Where(a => eventIds.Contains(a.EventId) && a.Status != AssignmentStatus.Declined)
            .GroupBy(a => a.EventId)
            .Select(g => new { EventId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.EventId, x => x.Count, ct);

        var items = events.Select(e => new EventListItemDto(
            e.Id, e.Title, e.Venue, e.StartAt, e.EndAt,
            e.Status.ToString(), e.MaxCrew,
            crewCounts.GetValueOrDefault(e.Id, 0), e.CreatedAt
        )).ToList();

        return Result.Success(new PagedEventResult(items, total, req.Page, req.PageSize));
    }
}
