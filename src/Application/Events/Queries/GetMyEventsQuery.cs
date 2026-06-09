using EventWOS.Application.Events.DTOs;
using EventWOS.Application.Interfaces;
using EventWOS.Domain.Enums;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;
using EventWOS.Domain.Rules;

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
        List<Guid> assignedEventIds;

        if (req.Role == UserRole.Vendor)
        {
            // Vendor sees events where they have at least one ACTIVE relationship.
            // Rejected / declined rows are filtered out so a vendor who was
            // rejected from an event stops seeing it in "My Events".
            assignedEventIds = await _db.EventAssignments
                .AsNoTracking()
                .Where(a => a.VendorId == req.UserId
                         && !a.IsDeleted
                         && a.Status != AssignmentStatus.Declined
                         && a.Status != AssignmentStatus.RejectedByManager
                         && a.Status != AssignmentStatus.RejectedByVendor)
                .Select(a => a.EventId)
                .Distinct()
                .ToListAsync(ct);
        }
        else
        {
            // Crew sees events they are personally assigned to (active only)
            assignedEventIds = await _db.EventAssignments
                .AsNoTracking()
                .Where(a => a.CrewId == req.UserId
                         && !a.IsDeleted
                         && a.Status != AssignmentStatus.Declined
                         && a.Status != AssignmentStatus.RejectedByManager
                         && a.Status != AssignmentStatus.RejectedByVendor)
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
                _db.EventAssignments.Count(a => a.EventId == e.Id
                                              && !a.IsDeleted
                                              && a.CrewId != null
                                              && a.Status != AssignmentStatus.Declined
                                              && a.Status != AssignmentStatus.RejectedByVendor
                                              && a.Status != AssignmentStatus.RejectedByManager
                                              && a.Status != AssignmentStatus.NoShow),
                e.CreatedAt,
                // Phase D step 21: confirmed-only count, inline so EF can
                // translate it. Same predicate as
                // AssignmentCapacityRules.IsConfirmed but EF in projection
                // sub-queries doesn't accept the static Expression form.
                _db.EventAssignments.Count(a => a.EventId == e.Id
                                              && !a.IsDeleted
                                              && a.CrewId != null
                                              && (a.Status == AssignmentStatus.ManagerApproved
                                               || a.Status == AssignmentStatus.Confirmed
                                               || a.Status == AssignmentStatus.Attended))))
            .ToListAsync(ct);

        return Result.Success(new PagedEventResult(items, total, req.Page, req.PageSize));
    }
}
