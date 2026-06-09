using EventWOS.Application.Interfaces;
using EventWOS.Domain.Enums;
using EventWOS.Domain.Rules;
using EventWOS.Shared.Common;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Attendance.Queries;

/// <summary>
/// Phase D step 22: one-row-per-event attendance rollup. Powers the
/// "Summary" tab on the admin <c>/attendance</c> page and the
/// summary Excel export.
///
/// One row = one event. Filters mirror the Logs tab's date/search/status
/// filters so admins can scope the summary the same way (e.g. "all
/// events in June" or "all completed events").
/// </summary>
public sealed record EventAttendanceSummaryRow(
    Guid     EventId,
    string   EventTitle,
    string   Venue,
    DateTime StartAt,
    DateTime EndAt,
    string   Status,           // EventStatus.ToString()
    int      MaxCrew,          // capacity from shifts
    int      ConfirmedCrew,    // ManagerApproved / Confirmed / Attended
    int      CheckedIn,        // unique crew with ≥1 CheckIn
    int      CheckedOut,       // unique crew with ≥1 CheckOut
    int      AdminOverrides,   // unique crew with ≥1 AdminOverride record
    int      Attended,         // assignments in Attended status (post-event truth)
    int      NoShows,          // effective no-shows: Approved/Confirmed without check-in (Completed events only)
    decimal  AttendancePercent // Attended / ConfirmedCrew (0–100; 0 when ConfirmedCrew == 0)
);

public sealed record GetAttendanceOverallSummaryQuery(
    string?      Search   = null,        // event title / venue contains
    EventStatus? Status   = null,        // filter by event status
    DateTime?    From     = null,        // event.StartAt >= From
    DateTime?    To       = null,        // event.StartAt <  To+1d
    string?      SortBy   = "StartAt",   // StartAt | EventTitle | AttendancePercent | Attended | NoShows
    bool         SortDesc = true,
    int          Page     = 1,
    int          PageSize = 20,
    bool         All      = false        // export-only
) : IRequest<Result<PagedResult<EventAttendanceSummaryRow>>>;

public sealed class GetAttendanceOverallSummaryHandler
    : IRequestHandler<GetAttendanceOverallSummaryQuery, Result<PagedResult<EventAttendanceSummaryRow>>>
{
    private readonly IAppDbContext _db;
    public GetAttendanceOverallSummaryHandler(IAppDbContext db) => _db = db;

    public async Task<Result<PagedResult<EventAttendanceSummaryRow>>> Handle(
        GetAttendanceOverallSummaryQuery req, CancellationToken ct)
    {
        var eventsQ = _db.Events.AsQueryable();

        if (!string.IsNullOrWhiteSpace(req.Search))
        {
            var s = req.Search.Trim();
            eventsQ = eventsQ.Where(e => e.Title.Contains(s) || e.Venue.Contains(s));
        }
        if (req.Status.HasValue) eventsQ = eventsQ.Where(e => e.Status == req.Status.Value);
        if (req.From.HasValue)   eventsQ = eventsQ.Where(e => e.StartAt >= req.From.Value);
        if (req.To.HasValue)
        {
            var toEnd = req.To.Value.Date.AddDays(1);
            eventsQ = eventsQ.Where(e => e.StartAt < toEnd);
        }

        var total = await eventsQ.CountAsync(ct);

        // We need to project counts per event, but Postgres + EF Core 9
        // chokes on multi-subquery projections inside Select(new {…}) when
        // mixing different predicates. So we do this in three round-trips:
        //   (1) the event basics for the current page,
        //   (2) one grouped Count of assignments by status,
        //   (3) one grouped Count of attendance records by action.
        // Then we stitch in memory. Cheap — the page is at most 20 events.
        var page = req.All
            ? await ApplySortNoPage(eventsQ, req).ToListAsync(ct)
            : await ApplySortNoPage(eventsQ, req)
                .Skip((req.Page - 1) * req.PageSize)
                .Take(req.PageSize)
                .ToListAsync(ct);

        var eventIds = page.Select(e => e.Id).ToList();

        // (2) assignments per event, grouped by status
        var assignmentsByEvent = await _db.EventAssignments
            .Where(a => eventIds.Contains(a.EventId) && !a.IsDeleted)
            .GroupBy(a => new { a.EventId, a.Status })
            .Select(g => new { g.Key.EventId, g.Key.Status, Count = g.Count() })
            .ToListAsync(ct);

        // Confirmed-crew predicate must also require CrewId != null
        // (placeholders never count as "approved crew"). Re-query the
        // narrow case directly with the rule we already use page-wide.
        var confirmedByEvent = await _db.EventAssignments
            .Where(a => eventIds.Contains(a.EventId))
            .Where(AssignmentCapacityRules.IsConfirmed)
            .GroupBy(a => a.EventId)
            .Select(g => new { EventId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.EventId, x => x.Count, ct);

        // (3) attendance records per event/action. Unique crew because
        // some events have multiple check-ins per crew (bug? feature?
        // either way the count of *distinct people* is what admins want).
        var attendanceByEvent = await _db.AttendanceRecords
            .Where(r => eventIds.Contains(r.EventId))
            .GroupBy(r => new { r.EventId, r.Action })
            .Select(g => new { g.Key.EventId, g.Key.Action, DistinctCrew = g.Select(r => r.CrewId).Distinct().Count() })
            .ToListAsync(ct);

        var attendanceLookup = attendanceByEvent
            .ToDictionary(x => (x.EventId, x.Action), x => x.DistinctCrew);

        var rows = page.Select(e =>
        {
            int CountStatus(AssignmentStatus s) =>
                assignmentsByEvent.FirstOrDefault(a => a.EventId == e.Id && a.Status == s)?.Count ?? 0;

            int CountAction(AttendanceAction a) =>
                attendanceLookup.GetValueOrDefault((e.Id, a), 0);

            var confirmed = confirmedByEvent.GetValueOrDefault(e.Id, 0);
            var attended  = CountStatus(AssignmentStatus.Attended);

            // No-shows: effective for Completed events (anyone who was
            // approved but never made it to Attended). For everything
            // else, only count rows actually flagged NoShow — pre-event
            // counts shouldn't penalise people who haven't been given
            // a chance to show up yet.
            int noShows;
            if (e.Status == EventStatus.Completed)
            {
                noShows = CountStatus(AssignmentStatus.NoShow)
                        + CountStatus(AssignmentStatus.ManagerApproved)
                        + CountStatus(AssignmentStatus.Confirmed)
                        - CountAction(AttendanceAction.AdminOverride); // overrides flip a no-show back to attended in the count
                if (noShows < 0) noShows = 0;
            }
            else
            {
                noShows = CountStatus(AssignmentStatus.NoShow);
            }

            var pct = confirmed == 0
                ? 0m
                : Math.Round((decimal)attended * 100m / confirmed, 1);

            return new EventAttendanceSummaryRow(
                e.Id, e.Title, e.Venue, e.StartAt, e.EndAt,
                e.Status.ToString(),
                e.MaxCrew,
                confirmed,
                CountAction(AttendanceAction.CheckIn),
                CountAction(AttendanceAction.CheckOut),
                CountAction(AttendanceAction.AdminOverride),
                attended,
                noShows,
                pct);
        }).ToList();

        // Re-sort in memory on derived columns that EF can't see.
        rows = (req.SortBy?.ToLowerInvariant(), req.SortDesc) switch
        {
            ("attendancepercent", true)  => rows.OrderByDescending(r => r.AttendancePercent).ToList(),
            ("attendancepercent", false) => rows.OrderBy(r => r.AttendancePercent).ToList(),
            ("attended",          true)  => rows.OrderByDescending(r => r.Attended).ToList(),
            ("attended",          false) => rows.OrderBy(r => r.Attended).ToList(),
            ("noshows",           true)  => rows.OrderByDescending(r => r.NoShows).ToList(),
            ("noshows",           false) => rows.OrderBy(r => r.NoShows).ToList(),
            _                            => rows // EF-side sort already applied for StartAt / EventTitle
        };

        return Result.Success(PagedResult<EventAttendanceSummaryRow>.Create(
            rows, total, req.Page, req.All ? Math.Max(rows.Count, 1) : req.PageSize));
    }

    private static IQueryable<Domain.Entities.Event> ApplySortNoPage(
        IQueryable<Domain.Entities.Event> q, GetAttendanceOverallSummaryQuery req) =>
        (req.SortBy?.ToLowerInvariant(), req.SortDesc) switch
        {
            ("eventtitle", true)  => q.OrderByDescending(e => e.Title),
            ("eventtitle", false) => q.OrderBy(e => e.Title),
            (_,            false) => q.OrderBy(e => e.StartAt),
            _                     => q.OrderByDescending(e => e.StartAt),
        };
}
