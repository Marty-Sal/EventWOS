using EventWOS.Application.Events.DTOs;
using EventWOS.Application.Interfaces;
using EventWOS.Domain.Enums;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Events.Queries;

public sealed record GetEventAssignmentsQuery(
    Guid EventId,
    int Page = 1, int PageSize = 50,
    AssignmentStatus? Status = null
) : IRequest<Result<PagedAssignmentResult>>;

public sealed class GetEventAssignmentsHandler : IRequestHandler<GetEventAssignmentsQuery, Result<PagedAssignmentResult>>
{
    private readonly IAppDbContext _db;
    public GetEventAssignmentsHandler(IAppDbContext db) => _db = db;

    public async Task<Result<PagedAssignmentResult>> Handle(GetEventAssignmentsQuery req, CancellationToken ct)
    {
        var query = _db.EventAssignments
            .Include(a => a.Crew)
            .Include(a => a.Vendor)
            .Include(a => a.Event)
            .Where(a => a.EventId == req.EventId);

        if (req.Status.HasValue)
            query = query.Where(a => a.Status == req.Status.Value);

        var total = await query.CountAsync(ct);

        var items = await query
            .OrderBy(a => a.CreatedAt)
            .Skip((req.Page - 1) * req.PageSize)
            .Take(req.PageSize)
            .Select(a => new EventAssignmentDto(
                a.Id, a.EventId, a.Event.Title, a.Event.Status.ToString(),
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
                a.VendorRating, a.RatedAt, a.AttendanceNote, a.ShiftId, _db.EventShifts.Where(s => s.Id == a.ShiftId).Select(s => s.ScopeOfWork.Name).FirstOrDefault()))
            .ToListAsync(ct);

        return Result.Success(new PagedAssignmentResult(items, total, req.Page, req.PageSize));
    }
}
