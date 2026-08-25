using EventOpsOracle.Application.Events.DTOs;
using EventOpsOracle.Application.Interfaces;
using EventOpsOracle.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventOpsOracle.Application.Events.Queries;

/// <summary>Returns all assignments for the authenticated crew member.</summary>
public sealed record GetMyAssignmentsQuery(Guid CrewId, int Page = 1, int PageSize = 20)
    : IRequest<Result<PagedAssignmentResult>>;

public sealed class GetMyAssignmentsHandler : IRequestHandler<GetMyAssignmentsQuery, Result<PagedAssignmentResult>>
{
    private readonly IAppDbContext _db;
    public GetMyAssignmentsHandler(IAppDbContext db) => _db = db;

    public async Task<Result<PagedAssignmentResult>> Handle(GetMyAssignmentsQuery req, CancellationToken ct)
    {
        // Count must apply the same live-shift filter as the page below, or the
        // pager promises rows the list will not show.
        var total = await _db.EventAssignments.CountAsync(
            a => a.CrewId == req.CrewId
              && (a.ShiftId == null || _db.EventShifts.Any(s => s.Id == a.ShiftId)), ct);

        var items = await _db.EventAssignments
            .Include(a => a.Event)
            .Include(a => a.Vendor)
            .Include(a => a.Crew)
            .Where(a => a.CrewId == req.CrewId)
            // Same rule as the vendor side: a deleted shift is not work.
            .Where(a => a.ShiftId == null || _db.EventShifts.Any(s => s.Id == a.ShiftId))
            .OrderByDescending(a => a.Event.StartAt)
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
                a.VendorRating, a.RatedAt, a.AttendanceNote, a.ShiftId, _db.EventShifts.Where(s => s.Id == a.ShiftId).Select(s => (string?)s.ScopeOfWork.Name).FirstOrDefault(), _db.EventShifts.Where(s => s.Id == a.ShiftId).Select(s => (DateTime?)s.StartAt).FirstOrDefault(), _db.EventShifts.Where(s => s.Id == a.ShiftId).Select(s => s.EndAt).FirstOrDefault()))
            .ToListAsync(ct);

        return Result.Success(new PagedAssignmentResult(items, total, req.Page, req.PageSize));
    }
}
