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
    IReadOnlyList<RecentActivityDto>   RecentActivity,
    IReadOnlyList<UpcomingEventDto>    UpcomingEvents,
    IReadOnlyList<TopVendorDto>        TopVendors,
    IReadOnlyList<NeedsStaffingDto>    NeedsStaffing
);

public sealed record RecentActivityDto(string Action, string Actor, string Target, DateTime At);

public sealed record UpcomingEventDto(
    Guid     Id,
    string   Title,
    string   Venue,
    DateTime StartAt,
    int      AssignedCrew,
    int      MaxCrew,
    string   Status
);

public sealed record NeedsStaffingDto(
    Guid     EventId,
    string   EventTitle,
    string   Venue,
    DateTime StartAt,
    Guid     VendorId,
    string   VendorName,
    int      MaxCrew,
    int      AssignedSoFar
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
        // ── 1. Event counts (simple GroupBy on primitive enum — EF safe) ─────
        var eventGroups = await _db.Events
            .GroupBy(e => e.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        int EC(EventStatus s) => eventGroups.FirstOrDefault(x => x.Status == s)?.Count ?? 0;
        int totalEvents = eventGroups.Sum(x => x.Count);

        // ── 2. User counts by role ────────────────────────────────────────────
        var userGroups = await _db.Users
            .GroupBy(u => u.Role)
            .Select(g => new { Role = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        int totalCrew    = userGroups.FirstOrDefault(x => x.Role == UserRole.Crew)?.Count   ?? 0;
        int totalVendors = userGroups.FirstOrDefault(x => x.Role == UserRole.Vendor)?.Count ?? 0;

        // ── 3. Assignment counts (GroupBy primitive enum) ─────────────────────
        var assignGroups = await _db.EventAssignments
            .GroupBy(a => a.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        int AC(AssignmentStatus s) => assignGroups.FirstOrDefault(x => x.Status == s)?.Count ?? 0;
        int totalAssignments     = assignGroups.Sum(x => x.Count);
        int confirmedAssignments = AC(AssignmentStatus.Confirmed) + AC(AssignmentStatus.Attended);
        int pendingAssignments   = AC(AssignmentStatus.Invited);
        int declinedAssignments  = AC(AssignmentStatus.Declined);

        // ── 4. Attendance ─────────────────────────────────────────────────────
        int totalCheckIns = await _db.AttendanceRecords
            .CountAsync(a => a.Action == AttendanceAction.CheckIn, ct);

        double attendanceRate = confirmedAssignments > 0
            ? Math.Round((double)totalCheckIns / confirmedAssignments * 100, 1)
            : 0;

        // ── 5. Recent audit activity (simple projection — no nav props) ────────
        var recentActivity = await _db.AuditLogs
            .OrderByDescending(a => a.OccurredAt)
            .Take(10)
            .Select(a => new RecentActivityDto(
                a.Action.ToString(),
                a.EntityType,
                a.EntityId ?? "—",
                a.OccurredAt))
            .ToListAsync(ct);

        // ── 6. Upcoming events — project without complex nav includes ─────────
        var now = DateTime.UtcNow;

        // Fetch event basics
        var upcomingRaw = await _db.Events
            .Where(e => (e.Status == EventStatus.Published || e.Status == EventStatus.InProgress)
                        && e.StartAt >= now)
            .OrderBy(e => e.StartAt)
            .Take(5)
            .Select(e => new
            {
                e.Id,
                e.Title,
                e.Venue,
                e.StartAt,
                e.MaxCrew,
                e.Status
            })
            .ToListAsync(ct);

        // Fetch assignment counts for those events separately (avoids sub-query translation issues)
        var upcomingIds = upcomingRaw.Select(e => e.Id).ToList();

        var assignCountPerEvent = await _db.EventAssignments
            .Where(a => upcomingIds.Contains(a.EventId)
                     && (a.Status == AssignmentStatus.Confirmed
                      || a.Status == AssignmentStatus.Attended))
            .GroupBy(a => a.EventId)
            .Select(g => new { EventId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var assignLookup = assignCountPerEvent.ToDictionary(x => x.EventId, x => x.Count);

        var upcoming = upcomingRaw.Select(e => new UpcomingEventDto(
            e.Id, e.Title, e.Venue, e.StartAt,
            assignLookup.GetValueOrDefault(e.Id, 0),
            e.MaxCrew,
            e.Status.ToString()
        )).ToList();

        // ── 7. Top vendors — avoid GroupBy on navigation props ────────────────
        // Fetch all assignments with VendorId + Status as raw scalars
        var vendorAssignRaw = await _db.EventAssignments
            .Select(a => new { a.VendorId, a.CrewId, a.Status })
            .ToListAsync(ct);

        // Group in memory (safe — no nav prop translation needed)
        var vendorGroups = vendorAssignRaw
            .Where(a => a.VendorId != null)
            .GroupBy(a => a.VendorId!.Value)
            .Select(g => new
            {
                VendorId         = g.Key,
                CrewCount        = g.Select(a => a.CrewId).Distinct().Count(),
                AssignmentsCount = g.Count(),
                ConfirmedCount   = g.Count(a => a.Status == AssignmentStatus.Confirmed
                                             || a.Status == AssignmentStatus.Attended)
            })
            .OrderByDescending(v => v.AssignmentsCount)
            .Take(5)
            .ToList();

        // Resolve vendor names in one batch query
        var vendorIds   = vendorGroups.Select(v => v.VendorId).ToList();
        var vendorNames = await _db.Users
            .Where(u => vendorIds.Contains(u.Id))
            .Select(u => new { u.Id, u.FullName })
            .ToListAsync(ct);
        var nameLookup = vendorNames.ToDictionary(v => v.Id, v => v.FullName);

        var topVendors = vendorGroups.Select(v => new TopVendorDto(
            nameLookup.GetValueOrDefault(v.VendorId, "Unknown"),
            v.CrewCount,
            v.AssignmentsCount,
            v.AssignmentsCount > 0
                ? Math.Round((double)v.ConfirmedCount / v.AssignmentsCount * 100, 1)
                : 0
        )).ToList();


        // ── 8. Needs-Staffing — vendor-only placeholder rows (CrewId is null) ─
        // A vendor was assigned to an event but hasn't added crew yet.
        var needsStaffingRaw = await _db.EventAssignments
            .Where(a => a.CrewId == null
                     && a.VendorId != null
                     && a.Status != AssignmentStatus.Declined
                     && a.Status != AssignmentStatus.RejectedByManager
                     && a.Status != AssignmentStatus.RejectedByVendor)
            .Select(a => new { a.EventId, a.VendorId })
            .ToListAsync(ct);

        var nsEventIds  = needsStaffingRaw.Select(x => x.EventId).Distinct().ToList();
        var nsVendorIds = needsStaffingRaw.Select(x => x.VendorId!.Value).Distinct().ToList();

        var nsEvents = await _db.Events
            .Where(e => nsEventIds.Contains(e.Id)
                     && e.Status != EventStatus.Completed
                     && e.Status != EventStatus.Cancelled)
            .Select(e => new { e.Id, e.Title, e.Venue, e.StartAt, e.MaxCrew })
            .ToListAsync(ct);
        var nsEventLookup = nsEvents.ToDictionary(e => e.Id);

        var nsVendorNames = await _db.Users
            .Where(u => nsVendorIds.Contains(u.Id))
            .Select(u => new { u.Id, u.FullName })
            .ToListAsync(ct);
        var nsVendorLookup = nsVendorNames.ToDictionary(v => v.Id, v => v.FullName);

        // How many real crew the vendor has already added per (event, vendor)
        var realCrewCounts = await _db.EventAssignments
            .Where(a => nsEventIds.Contains(a.EventId)
                     && a.CrewId != null
                     && a.Status != AssignmentStatus.Declined)
            .GroupBy(a => new { a.EventId, a.VendorId })
            .Select(g => new { g.Key.EventId, g.Key.VendorId, Count = g.Count() })
            .ToListAsync(ct);

        var needsStaffing = needsStaffingRaw
            .Where(r => nsEventLookup.ContainsKey(r.EventId))
            .Select(r =>
            {
                var ev = nsEventLookup[r.EventId];
                var vendorId = r.VendorId!.Value;
                var assignedSoFar = realCrewCounts
                    .FirstOrDefault(rc => rc.EventId == r.EventId && rc.VendorId == vendorId)?.Count ?? 0;
                return new NeedsStaffingDto(
                    ev.Id, ev.Title, ev.Venue, ev.StartAt,
                    vendorId, nsVendorLookup.GetValueOrDefault(vendorId, "Unknown"),
                    ev.MaxCrew, assignedSoFar);
            })
            // Hide rows where the vendor has already finished staffing.
            // Placeholders are kept permanently as the vendor's anchor row,
            // so we use the assigned-vs-capacity ratio to decide visibility.
            .Where(n => n.MaxCrew == 0
                         ? n.AssignedSoFar == 0
                         : n.AssignedSoFar < n.MaxCrew)
            // De-dupe by (event, vendor) since placeholders may appear once we
            // keep them around indefinitely.
            .GroupBy(n => new { n.EventId, n.VendorId })
            .Select(g => g.First())
            .OrderBy(n => n.StartAt)
            .Take(10)
            .ToList();

        return Result.Success(new DashboardStatsDto(
            totalEvents,
            EC(EventStatus.Draft), EC(EventStatus.Published),
            EC(EventStatus.InProgress), EC(EventStatus.Completed), EC(EventStatus.Cancelled),
            totalCrew, totalVendors,
            totalAssignments, confirmedAssignments, pendingAssignments, declinedAssignments,
            totalCheckIns, attendanceRate,
            recentActivity, upcoming, topVendors, needsStaffing));
    }
}
