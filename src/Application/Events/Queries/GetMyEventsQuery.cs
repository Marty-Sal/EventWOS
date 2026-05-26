using EventWOS.Application.Events.DTOs;
using EventWOS.Application.Interfaces;
using EventWOS.Domain.Enums;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Events.Queries;

/// <summary>
/// Returns all events the authenticated user is associated with.
/// - Crew: events they are assigned to (by CrewId in EventAssignments)
/// - Vendor: events they have assigned crew to (by VendorId in EventAssignments)
/// Does NOT require events:read — works with profile:read for both roles.
/// </summary>
public sealed record GetMyEventsQuery(Guid UserId, UserRole Role, int Page = 1, int PageSize = 20)
    : IRequest<Result<PagedEventResult>>;

public sealed class GetMyEventsHandler : IRequestHandler<GetMyEventsQuery, Result<PagedEventResult>>
{
    private readonly IAppDbContext _db;
    public GetMyEventsHandler(IAppDbContext db) => _db = db;

    public async Task<Result<PagedEventResult>> Handle(GetMyEventsQuery req, CancellationToken ct)
    {
        IQueryable<int> eventIdQuery;

        List<Guid> assignedEventIds;

        if (req.Role == UserRole.Vendor)
        {
            // Vendor sees events where they have crew assignments
            assignedEventIds = await _db.EventAssignments
                .AsNoTracking()
                .Where(a => a.VendorId == req.UserId && !a.IsDeleted)
                .Select(a => a.EventId)
                .Distinct()
                .ToListAsync(ct);
        }
        else
        {
            // Crew sees events they are personally assigned to
            assignedEventIds = await _db.EventAssignments
                .AsNoTracking()
                .Where(a => a.CrewId == req.UserId && !a.IsDeleted)
                .Select(a => a.EventId)
                .Distinct()
                .ToListAsync(ct);
        }

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
                _db.EventAssignments.Count(a => a.EventId == e.Id && !a.IsDeleted),
                e.CreatedAt))
            .ToListAsync(ct);

        return Result.Success(new PagedEventResult(items, total, req.Page, req.PageSize));
    }
}
