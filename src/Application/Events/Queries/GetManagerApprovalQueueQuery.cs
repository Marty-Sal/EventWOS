using EventWOS.Application.Interfaces;
using EventWOS.Domain.Enums;
using EventWOS.Shared.Common;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Events.Queries;

public sealed record ManagerApprovalItemDto(
    Guid     AssignmentId,
    Guid     EventId,
    string   EventTitle,
    string   EventVenue,
    DateTime EventStartAt,
    Guid     CrewId,
    string   CrewName,
    string   CrewMobile,
    decimal  DisciplineScore,
    int      EventsAttended,
    Guid     VendorId,
    string   VendorName,
    DateTime CrewRespondedAt,
    DateTime? VendorReviewedAt,
    string   Status
);

public sealed record GetManagerApprovalQueueQuery(
    int PageNumber = 1,
    int PageSize   = 20
) : IRequest<Result<PagedResult<ManagerApprovalItemDto>>>;

public sealed class GetManagerApprovalQueueHandler
    : IRequestHandler<GetManagerApprovalQueueQuery, Result<PagedResult<ManagerApprovalItemDto>>>
{
    private readonly IAppDbContext _db;
    public GetManagerApprovalQueueHandler(IAppDbContext db) => _db = db;

    public async Task<Result<PagedResult<ManagerApprovalItemDto>>> Handle(
        GetManagerApprovalQueueQuery req, CancellationToken ct)
    {
        var query = _db.EventAssignments
            .Where(a => a.Status == AssignmentStatus.PendingManagerApproval)
            .Include(a => a.Event)
            .Include(a => a.Crew)
            .Include(a => a.Vendor);

        var total = await query.CountAsync(ct);

        var items = await query
            .OrderBy(a => a.VendorReviewedAt)
            .Skip((req.PageNumber - 1) * req.PageSize)
            .Take(req.PageSize)
            .Select(a => new ManagerApprovalItemDto(
                a.Id,
                a.EventId,
                a.Event.Title,
                a.Event.Venue,
                a.Event.StartAt,
                a.CrewId,
                a.Crew.FullName,
                a.Crew.Mobile,
                a.Crew.DisciplineScore,
                a.Crew.EventsAttended,
                a.VendorId,
                a.Vendor.FullName,
                a.CrewRespondedAt ?? a.CreatedAt,
                a.VendorReviewedAt,
                a.Status.ToString()))
            .ToListAsync(ct);

        return Result.Success(PagedResult<ManagerApprovalItemDto>.Create(
            items, total, req.PageNumber, req.PageSize));
    }
}
