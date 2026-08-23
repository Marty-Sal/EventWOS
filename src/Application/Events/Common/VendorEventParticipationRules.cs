using EventWOS.Domain.Enums;

namespace EventWOS.Application.Events.Common;

/// <summary>
/// Single source of truth for "which events does a vendor count as theirs",
/// and by extension how many of those are done.
///
/// This exists because the number was previously read from a stored
/// User.EventsCompleted counter that nothing ever incremented, so every
/// vendor dashboard showed 0 forever. Rather than start maintaining that
/// counter (which then needs a backfill, and silently drifts the moment an
/// increment is missed or applied twice), the value is computed on read from
/// the assignment rows that already exist.
///
/// The participation rule below is deliberately the SAME rule
/// GetMyEventsQuery uses to decide which events appear under "My Events".
/// Both call into <see cref="InactiveStatuses"/>, so the dashboard tile and
/// the "Completed" chip on My Events cannot drift apart and start reporting
/// different totals for the same vendor -- which is exactly the class of bug
/// this replaced.
/// </summary>
public static class VendorEventParticipationRules
{
    /// <summary>
    /// Assignment statuses that mean the relationship is OFF: the vendor
    /// either turned the event down or was rejected. Anything else (Invited,
    /// CrewConfirmed, VendorApproved, PendingManagerApproval, ManagerApproved,
    /// Confirmed, Attended, NoShow) still counts as being on the event.
    ///
    /// Exposed as an array so EF can translate
    /// `!InactiveStatuses.Contains(a.Status)` into a plain SQL NOT IN, which
    /// lets the database-side queries reuse this list verbatim instead of
    /// re-typing the status names at each call site.
    /// </summary>
    public static readonly AssignmentStatus[] InactiveStatuses =
    {
        AssignmentStatus.Declined,
        AssignmentStatus.RejectedByVendor,
        AssignmentStatus.RejectedByManager
    };

    /// <summary>In-memory counterpart of the EF filter above.</summary>
    public static bool IsActiveParticipation(AssignmentStatus status)
        => Array.IndexOf(InactiveStatuses, status) < 0;

    /// <summary>
    /// An event counts as "done" for a vendor when the vendor is still
    /// actively on it and the event itself has been completed. Cancelled
    /// events never count, and an event that is merely InProgress does not
    /// count until an admin completes it.
    ///
    /// Note this counts the EVENT being delivered, not per-head attendance --
    /// a vendor whose crew all no-showed a completed event still has the
    /// event on their record. Crew-side attendance is tracked separately by
    /// User.EventsAttended.
    /// </summary>
    public static bool CountsAsDone(AssignmentStatus assignmentStatus, EventStatus eventStatus)
        => IsActiveParticipation(assignmentStatus) && eventStatus == EventStatus.Completed;

    /// <summary>
    /// Flattened assignment row: one per (vendor, event, assignment). A vendor
    /// normally has several rows per event -- the seat-quota placeholder plus
    /// one per crew member placed -- hence the DistinctBy on EventId below.
    /// </summary>
    public readonly record struct ParticipationRow(
        Guid              VendorId,
        Guid              EventId,
        AssignmentStatus  AssignmentStatus,
        EventStatus       EventStatus);

    /// <summary>
    /// Distinct completed events for a single vendor. Multiple crew placed on
    /// one event is still one event done.
    /// </summary>
    public static int CountEventsDone(IEnumerable<ParticipationRow> rows)
        => rows.Where(r => CountsAsDone(r.AssignmentStatus, r.EventStatus))
               .Select(r => r.EventId)
               .Distinct()
               .Count();

    /// <summary>
    /// Batch form for the admin vendor list: one grouped pass over the rows
    /// for the whole page of vendors, so the list stays a fixed number of
    /// queries instead of one per vendor.
    /// </summary>
    public static Dictionary<Guid, int> CountEventsDonePerVendor(IEnumerable<ParticipationRow> rows)
        => rows.Where(r => CountsAsDone(r.AssignmentStatus, r.EventStatus))
               .GroupBy(r => r.VendorId)
               .ToDictionary(g => g.Key, g => g.Select(r => r.EventId).Distinct().Count());

    /// <summary>
    /// Same three buckets the Admin dashboard's "Total Events" tile shows
    /// (live / upcoming / completed), scoped to a single vendor or crew
    /// member's own events instead of every event in the system. Cancelled
    /// events never get a bucket (nothing to show), and Draft never reaches
    /// here because non-admins are never assigned to a Draft event.
    /// </summary>
    public readonly record struct EventCountSummary(int Live, int Upcoming, int Completed);

    /// <summary>
    /// Buckets a set of DISTINCT (EventId, EventStatus) pairs -- already
    /// filtered to active participation -- by event lifecycle state. Shared
    /// by the vendor and crew loaders so "live"/"upcoming"/"completed" mean
    /// exactly the same thing on both dashboards.
    /// </summary>
    public static EventCountSummary SummarizeByStatus(IEnumerable<(Guid EventId, EventStatus Status)> distinctEvents)
    {
        var byStatus = distinctEvents.GroupBy(x => x.Status).ToDictionary(g => g.Key, g => g.Count());
        int C(EventStatus s) => byStatus.TryGetValue(s, out var n) ? n : 0;
        return new EventCountSummary(C(EventStatus.InProgress), C(EventStatus.Published), C(EventStatus.Completed));
    }

    /// <summary>Vendor form: filters to active participation and de-dupes multi-row events first.</summary>
    public static EventCountSummary Summarize(IEnumerable<ParticipationRow> rows)
        => SummarizeByStatus(
               rows.Where(r => IsActiveParticipation(r.AssignmentStatus))
                   .Select(r => (r.EventId, r.EventStatus))
                   .Distinct());
}
