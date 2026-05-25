using EventWOS.Application.Interfaces;
using EventWOS.Domain.Enums;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Analytics.Queries;

// ─── DTOs ────────────────────────────────────────────────────────────────────

public sealed record DashboardStatsDto(
    int TotalEvents,
    int DraftEvents,
    int PublishedEvents,
    int InProgressEvents,
    int CompletedEvents,
    int CancelledEvents,

    int TotalCrew,
    int TotalVendors,
    int TotalAssignments,
    int ConfirmedAssignments,
    int PendingAssignments,
    int DeclinedAssignments,

    int    TotalCheckIns,
    double AttendanceRate,

    IReadOnlyList<RecentActivityDto> RecentActivity,
    IReadOnlyList<UpcomingEventDto>  UpcomingEvents,
    IReadOnlyList<TopVendorDto>      TopVendors
);

public sealed record RecentActivityDto(string Action, string Actor, string Target, DateTime At);

public sealed record UpcomingEventDto(
    Guid   Id,
    string Title,
    string Venue,
    DateTime StartAt,
    int AssignedCrew,
    int MaxCrew,
    string Status
);

public sealed record TopVendorDto(
    string VendorName,
    int    CrewCount,
    int    AssignmentsCount,
    double ConfirmationRate
);

// ─── Query & Handler ─────────────────────────────────────────────────────────

public sealed record GetDashboardStatsQuery : IRequest<Result<DashboardStatsDto>>;

public sealed class GetDashboardStatsHandler
    : IRequestHandler<GetDashboardStatsQuery, Result<DashboardStatsDto>>
{
    private readonly IAppDbContext _db;
    public GetDashboardStatsHandler(IAppDbContext db) => _db = db;

    public async Task<Result<DashboardStatsDto>> Handle(
        GetDashboardStatsQuery _, CancellationToken ct)
    {
        // Event counts by status
        var eventCounts = await _db.Events
            .GroupBy(e => e.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        int EC(EventStatus s) => eventCounts.FirstOrDefault(x => x.Status == s)?.Count ?? 0;

        // User counts by role
        var userCounts = await _db.Users
            .GroupBy(u => u.Role)
            .Select(g => new { Role = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        // Assignment counts by status
        var assignCounts = await _db.EventAssignments
            .GroupBy(a => a.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        int AC(AssignmentStatus s) => assignCounts.FirstOrDefault(x => x.Status == s)?.Count ?? 0;

        int totalAssignments     = assignCounts.Sum(x => x.Count);
        int confirmedAssignments = AC(AssignmentStatus.Confirmed) + AC(AssignmentStatus.Attended);

        // Attendance
        int totalCheckIns = await _db.AttendanceRecords
            .CountAsync(a => a.Action == AttendanceAction.CheckIn, ct);

        double attendanceRate = confirmedAssignments > 0
            ? Math.Round((double)totalCheckIns / confirmedAssignments * 100, 1)
            : 0;

        // Recent audit activity
        var recentActivity = await _db.AuditLogs
            .OrderByDescending(a => a.OccurredAt)
            .Take(10)
            .Select(a => new RecentActivityDto(
                a.Action.ToString(),
                a.EntityType,
                a.EntityId ?? "—",
                a.OccurredAt))
            .ToListAsync(ct);

        // Upcoming published/in-progress events
        var now = DateTime.UtcNow;
        var upcoming = await _db.Events
            .Where(e => (e.Status == EventStatus.Published || e.Status == EventStatus.InProgress)
                        && e.StartAt >= now)
            .OrderBy(e => e.StartAt)
            .Take(5)
            .Select(e => new UpcomingEventDto(
                e.Id,
                e.Title,
                e.Venue,
                e.StartAt,
                e.Assignments.Count(a => a.Status == AssignmentStatus.Confirmed
                                      || a.Status == AssignmentStatus.Attended),
                e.MaxCrew,
                e.Status.ToString()))
            .ToListAsync(ct);

        // Top vendors by crew assigned
        var topVendors = await _db.EventAssignments
            .GroupBy(a => a.Vendor.FullName)
            .Select(g => new TopVendorDto(
                g.Key,
                g.Select(a => a.CrewId).Distinct().Count(),
                g.Count(),
                g.Count() == 0 ? 0 : Math.Round(
                    (double)g.Count(a => a.Status == AssignmentStatus.Confirmed
                                      || a.Status == AssignmentStatus.Attended)
                    / g.Count() * 100, 1)))
            .OrderByDescending(v => v.AssignmentsCount)
            .Take(5)
            .ToListAsync(ct);

        return Result.Success(new DashboardStatsDto(
            eventCounts.Sum(x => x.Count),
            EC(EventStatus.Draft), EC(EventStatus.Published),
            EC(EventStatus.InProgress), EC(EventStatus.Completed), EC(EventStatus.Cancelled),
            userCounts.FirstOrDefault(x => x.Role == UserRole.Crew)?.Count   ?? 0,
            userCounts.FirstOrDefault(x => x.Role == UserRole.Vendor)?.Count ?? 0,
            totalAssignments, confirmedAssignments,
            AC(AssignmentStatus.Invited), AC(AssignmentStatus.Declined),
            totalCheckIns, attendanceRate,
            recentActivity, upcoming, topVendors));
    }
}
