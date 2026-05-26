using EventWOS.Application.Events.DTOs;
using EventWOS.Application.Interfaces;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Events.Queries;

/// <summary>Returns all crew assignments that belong to the authenticated vendor.</summary>
public sealed record GetVendorAssignmentsQuery(Guid VendorId, int Page = 1, int PageSize = 20)
    : IRequest<Result<PagedAssignmentResult>>;

public sealed class GetVendorAssignmentsHandler
    : IRequestHandler<GetVendorAssignmentsQuery, Result<PagedAssignmentResult>>
{
    private readonly IAppDbContext _db;
    public GetVendorAssignmentsHandler(IAppDbContext db) => _db = db;

    public async Task<Result<PagedAssignmentResult>> Handle(
        GetVendorAssignmentsQuery req, CancellationToken ct)
    {
        var total = await _db.EventAssignments
            .CountAsync(a => a.VendorId == req.VendorId, ct);

        var items = await _db.EventAssignments
            .Include(a => a.Event)
            .Include(a => a.Vendor)
            .Include(a => a.Crew)
            .Where(a => a.VendorId == req.VendorId)
            .OrderByDescending(a => a.Event.StartAt)
            .Skip((req.Page - 1) * req.PageSize)
            .Take(req.PageSize)
            .Select(a => new EventAssignmentDto(
                a.Id, a.EventId, a.Event.Title,
                a.CrewId,
                a.Crew != null ? a.Crew.FullName : null,
                a.Crew != null ? a.Crew.Mobile   : null,
                a.Crew != null ? a.Crew.DisciplineScore : 0,
                a.Crew != null ? a.Crew.EventsAttended  : 0,
                a.Crew != null ? a.Crew.CrewRating      : null,
                a.Crew != null ? a.Crew.CrewRatingCount : 0,
                a.VendorId, a.Vendor != null ? a.Vendor.FullName : null,
                a.Status.ToString(),
                a.RejectionReason,
                a.CrewRespondedAt,
                a.VendorReviewedAt,
                a.ManagerReviewedAt,
                a.ConfirmedAt, a.DeclinedAt, a.CreatedAt,
                a.VendorRating, a.RatedAt))
            .ToListAsync(ct);

        return Result.Success(new PagedAssignmentResult(items, total, req.Page, req.PageSize));
    }
}
