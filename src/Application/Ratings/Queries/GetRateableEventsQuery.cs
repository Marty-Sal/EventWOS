using EventWOS.Application.Interfaces;
using EventWOS.Domain.Enums;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Ratings.Queries;

/// <summary>
/// A completed event this vendor worked, and the rating already given for it (if
/// any). The existing rating travels with the row so the picker can show what was
/// submitted rather than making the rater guess whether they have done this one.
/// </summary>
public sealed record RateableEventDto(
    Guid      EventId,
    string    EventTitle,
    string    Venue,
    DateTime  StartAt,
    bool      AlreadyRated,
    int?      Performance,
    int?      Cooperation,
    string?   Comment,
    DateTime? RatedAt);

/// <summary>
/// Which of a vendor's events may be rated right now.
///
/// Only COMPLETED events qualify: rating work that has not happened yet is
/// meaningless, and it is the same rule the write path enforces. Returning
/// ineligible events here would just produce a picker whose entries fail on
/// submit.
/// </summary>
public sealed record GetRateableEventsQuery(Guid VendorUserId)
    : IRequest<Result<IReadOnlyList<RateableEventDto>>>;

public sealed class GetRateableEventsHandler
    : IRequestHandler<GetRateableEventsQuery, Result<IReadOnlyList<RateableEventDto>>>
{
    private readonly IAppDbContext _db;
    public GetRateableEventsHandler(IAppDbContext db) => _db = db;

    public async Task<Result<IReadOnlyList<RateableEventDto>>> Handle(
        GetRateableEventsQuery req, CancellationToken ct)
    {
        // Both routes onto an event count, matching the write path's check exactly.
        // A vendor with a shift quota but no assignment row still worked the event.
        var viaAllocation = _db.VendorShiftAllocations
            .Where(a => a.VendorId == req.VendorUserId && !a.IsDeleted)
            .Select(a => a.Shift!.EventId);

        var viaAssignment = _db.EventAssignments
            .Where(a => a.VendorId == req.VendorUserId && !a.IsDeleted)
            .Select(a => a.EventId);

        var eventIds = await viaAllocation.Concat(viaAssignment).Distinct().ToListAsync(ct);
        if (eventIds.Count == 0)
            return Result.Success<IReadOnlyList<RateableEventDto>>(Array.Empty<RateableEventDto>());

        var events = await _db.Events
            .Where(e => eventIds.Contains(e.Id)
                     && e.Status == EventStatus.Completed
                     && !e.IsDeleted)
            .OrderByDescending(e => e.StartAt)
            .Select(e => new { e.Id, e.Title, e.Venue, e.StartAt })
            .ToListAsync(ct);

        var existing = await _db.Ratings
            .Where(r => r.SubjectUserId == req.VendorUserId
                     && r.SubjectType   == RatingSubjectType.Vendor
                     && eventIds.Contains(r.EventId))
            .Select(r => new { r.EventId, r.Performance, r.Cooperation, r.Comment, r.RatedAt })
            .ToListAsync(ct);

        var byEvent = existing.ToDictionary(r => r.EventId);

        var items = events.Select(e =>
        {
            var hit = byEvent.GetValueOrDefault(e.Id);
            return new RateableEventDto(
                e.Id, e.Title, e.Venue, e.StartAt,
                AlreadyRated: hit is not null,
                Performance:  hit?.Performance,
                Cooperation:  hit?.Cooperation,
                Comment:      hit?.Comment,
                RatedAt:      hit?.RatedAt);
        }).ToList();

        return Result.Success<IReadOnlyList<RateableEventDto>>(items);
    }
}
