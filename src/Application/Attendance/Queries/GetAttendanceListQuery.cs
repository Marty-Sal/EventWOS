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
    string?  RecordedBy,         // raw user id — kept for back-compat with existing UI
    string?  RecordedByName = null // Phase D step 22: friendly name for export + filter UX
);

// ── Admin/Manager: filterable list ───────────────────────────────────────────
// Phase D step 22: filters expanded from "EventId only" to a real filter bar
// (search / event / action / date range / sort). Pagination + back-compat
// params unchanged. All filters optional — handler defaults preserve the
// pre-step-22 behaviour exactly when only EventId/CrewId are passed.
public sealed record GetAttendanceListQuery(
    Guid?     EventId    = null,
    Guid?     CrewId     = null,
    string?   Search     = null,     // crew name / event title (case-insensitive contains)
    string?   Action     = null,     // "CheckIn" | "CheckOut" | "AdminOverride" — string for forward-compat
    DateTime? From       = null,     // RecordedAt >= From  (UTC)
    DateTime? To         = null,     // RecordedAt <  To.AddDays(1)  (UTC; inclusive day semantics)
    string?   SortBy     = "RecordedAt", // RecordedAt | CrewName | EventTitle | Action
    bool      SortDesc   = true,
    int       PageNumber = 1,
    int       PageSize   = 20,
    bool      All        = false     // export-only: bypass pagination (use with care).
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

        if (!string.IsNullOrWhiteSpace(req.Search))
        {
            var s = req.Search.Trim();
            query = query.Where(r => r.Crew.FullName.Contains(s) || r.Event.Title.Contains(s));
        }

        if (!string.IsNullOrWhiteSpace(req.Action)
            && Enum.TryParse<Domain.Enums.AttendanceAction>(req.Action, ignoreCase: true, out var act))
        {
            query = query.Where(r => r.Action == act);
        }

        if (req.From.HasValue) query = query.Where(r => r.RecordedAt >= req.From.Value);
        if (req.To.HasValue)
        {
            // Inclusive end-of-day: filter as < (To + 1 day) so a user picking
            // "To = today" gets every record from today including the last
            // minute. Cheaper than reaching for DateOnly on a DateTime column.
            var toEnd = req.To.Value.Date.AddDays(1);
            query = query.Where(r => r.RecordedAt < toEnd);
        }

        var total = await query.CountAsync(ct);

        // Sort. Whitelist comparison so callers can't inject arbitrary
        // expression trees; default to RecordedAt desc which matches the
        // old behaviour.
        query = (req.SortBy?.ToLowerInvariant(), req.SortDesc) switch
        {
            ("crewname",  true)  => query.OrderByDescending(r => r.Crew.FullName),
            ("crewname",  false) => query.OrderBy(r => r.Crew.FullName),
            ("eventtitle",true)  => query.OrderByDescending(r => r.Event.Title),
            ("eventtitle",false) => query.OrderBy(r => r.Event.Title),
            ("action",    true)  => query.OrderByDescending(r => r.Action),
            ("action",    false) => query.OrderBy(r => r.Action),
            (_, true)            => query.OrderByDescending(r => r.RecordedAt),
            (_, false)           => query.OrderBy(r => r.RecordedAt),
        };

        if (!req.All)
        {
            query = query.Skip((req.PageNumber - 1) * req.PageSize).Take(req.PageSize);
        }

        // RecordedByUserId is a string column. We join via the Users table to
        // surface a human name on the UI + Excel. Left-join semantics —
        // system-recorded rows (no user) just get null.
        var items = await query
            .Select(r => new
            {
                r.Id,
                r.AssignmentId,
                r.EventId,
                EventTitle = r.Event.Title,
                r.CrewId,
                CrewName   = r.Crew.FullName,
                Action     = r.Action.ToString(),
                r.RecordedAt,
                r.Location,
                r.RecordedByUserId,
                RecordedByName = r.RecordedByUserId == null
                    ? null
                    : _db.Users.Where(u => u.Id.ToString() == r.RecordedByUserId)
                              .Select(u => u.FullName)
                              .FirstOrDefault()
            })
            .ToListAsync(ct);

        var dtos = items.Select(x => new AttendanceListItemDto(
            x.Id, x.AssignmentId, x.EventId, x.EventTitle,
            x.CrewId, x.CrewName, x.Action,
            x.RecordedAt, x.Location, x.RecordedByUserId, x.RecordedByName)).ToList();

        return Result.Success(PagedResult<AttendanceListItemDto>.Create(
            dtos, total, req.PageNumber, req.All ? Math.Max(dtos.Count, 1) : req.PageSize));
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
                r.RecordedAt, r.Location, r.RecordedByUserId, null))
            .ToListAsync(ct);

        return Result.Success(PagedResult<AttendanceListItemDto>.Create(items, total, req.PageNumber, req.PageSize));
    }
}
