using EventWOS.Application.Events.DTOs;
using EventWOS.Application.Interfaces;
using EventWOS.Domain.Enums;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Events.Queries;

public enum VendorAssignmentMode
{
    All,             // every row attributed to this vendor (legacy default)
    Invitations,     // placeholder rows only — CrewId == null AND Status == Invited
    CrewAssignments  // rows with a real crew member — CrewId != null
}

/// <summary>Returns assignments that belong to the authenticated vendor, filterable by mode.</summary>
public sealed record GetVendorAssignmentsQuery(
    Guid VendorId,
    VendorAssignmentMode Mode = VendorAssignmentMode.All,
    int Page = 1,
    int PageSize = 20
) : IRequest<Result<PagedAssignmentResult>>;

public sealed class GetVendorAssignmentsHandler
    : IRequestHandler<GetVendorAssignmentsQuery, Result<PagedAssignmentResult>>
{
    private readonly IAppDbContext _db;
    public GetVendorAssignmentsHandler(IAppDbContext db) => _db = db;

    public async Task<Result<PagedAssignmentResult>> Handle(
        GetVendorAssignmentsQuery req, CancellationToken ct)
    {
        var q = _db.EventAssignments.AsQueryable().Where(a => a.VendorId == req.VendorId);

        q = req.Mode switch
        {
            VendorAssignmentMode.Invitations
                => q.Where(a => a.CrewId == null && a.Status == AssignmentStatus.Invited),
            VendorAssignmentMode.CrewAssignments
                => q.Where(a => a.CrewId != null),
            _ => q
        };

        var total = await q.CountAsync(ct);

        var items = await q
            .Include(a => a.Event)
            .Include(a => a.Vendor)
            .Include(a => a.Crew)
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
                a.VendorRating, a.RatedAt, a.AttendanceNote, a.ShiftId, _db.EventShifts.Where(s => s.Id == a.ShiftId).Select(s => s.ScopeOfWork.Name).FirstOrDefault()))
            .ToListAsync(ct);

        return Result.Success(new PagedAssignmentResult(items, total, req.Page, req.PageSize));
    }
}
