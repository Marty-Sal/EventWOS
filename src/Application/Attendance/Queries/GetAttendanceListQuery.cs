using EventWOS.Application.Interfaces;
using EventWOS.Shared.Common;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Attendance.Queries;

public sealed record AttendanceListItemDto(
    Guid     RecordId,
    Guid     AssignmentId,
    Guid     EventId,
    string   EventTitle,
    Guid     CrewId,
    string   CrewName,
    string   Action,
    DateTime RecordedAt,
    string?  Location,
    string?  RecordedBy
);

// ── Admin/Manager: filterable list ───────────────────────────────────────────
public sealed record GetAttendanceListQuery(
    Guid? EventId    = null,
    Guid? CrewId     = null,
    int   PageNumber = 1,
    int   PageSize   = 20
) : IRequest<Result<PagedResult<AttendanceListItemDto>>>;

public sealed class GetAttendanceListHandler
    : IRequestHandler<GetAttendanceListQuery, Result<PagedResult<AttendanceListItemDto>>>
{
    private readonly IAppDbContext _db;
    public GetAttendanceListHandler(IAppDbContext db) => _db = db;

    public async Task<Result<PagedResult<AttendanceListItemDto>>> Handle(
        GetAttendanceListQuery req, CancellationToken ct)
    {
        var query = _db.AttendanceRecords
            .Include(r => r.Event)
            .Include(r => r.Crew)
            .AsQueryable();

        if (req.EventId.HasValue) query = query.Where(r => r.EventId == req.EventId.Value);
        if (req.CrewId.HasValue)  query = query.Where(r => r.CrewId  == req.CrewId.Value);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(r => r.RecordedAt)
            .Skip((req.PageNumber - 1) * req.PageSize)
            .Take(req.PageSize)
            .Select(r => new AttendanceListItemDto(
                r.Id, r.AssignmentId, r.EventId, r.Event.Title,
                r.CrewId, r.Crew.FullName, r.Action.ToString(),
                r.RecordedAt, r.Location, r.RecordedByUserId))
            .ToListAsync(ct);

        return Result.Success(PagedResult<AttendanceListItemDto>.Create(items, total, req.PageNumber, req.PageSize));
    }
}

// ── Crew: my own records ──────────────────────────────────────────────────────
public sealed record GetMyAttendanceQuery(
    Guid UserId,
    int  PageNumber = 1,
    int  PageSize   = 20
) : IRequest<Result<PagedResult<AttendanceListItemDto>>>;

public sealed class GetMyAttendanceHandler
    : IRequestHandler<GetMyAttendanceQuery, Result<PagedResult<AttendanceListItemDto>>>
{
    private readonly IAppDbContext _db;
    public GetMyAttendanceHandler(IAppDbContext db) => _db = db;

    public async Task<Result<PagedResult<AttendanceListItemDto>>> Handle(
        GetMyAttendanceQuery req, CancellationToken ct)
    {
        var total = await _db.AttendanceRecords.Where(r => r.CrewId == req.UserId).CountAsync(ct);
        var items = await _db.AttendanceRecords
            .Include(r => r.Event)
            .Include(r => r.Crew)
            .Where(r => r.CrewId == req.UserId)
            .OrderByDescending(r => r.RecordedAt)
            .Skip((req.PageNumber - 1) * req.PageSize)
            .Take(req.PageSize)
            .Select(r => new AttendanceListItemDto(
                r.Id, r.AssignmentId, r.EventId, r.Event.Title,
                r.CrewId, r.Crew.FullName, r.Action.ToString(),
                r.RecordedAt, r.Location, r.RecordedByUserId))
            .ToListAsync(ct);

        return Result.Success(PagedResult<AttendanceListItemDto>.Create(items, total, req.PageNumber, req.PageSize));
    }
}
