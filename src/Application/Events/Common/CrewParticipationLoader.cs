using EventWOS.Application.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Events.Common;

/// <summary>
/// Crew-side counterpart of <see cref="VendorParticipationLoader"/>: same
/// "which events count as mine" rule (VendorEventParticipationRules.
/// InactiveStatuses -- a crew member who declined or was rejected is not on
/// the event), keyed on EventAssignment.CrewId instead of VendorId.
///
/// No DistinctBy is needed the way the vendor loader needs one: a vendor can
/// have several rows on the same event (the seat-quota placeholder plus one
/// per crew member placed), but a given crew member has at most one
/// assignment row per event.
/// </summary>
public static class CrewParticipationLoader
{
    public static async Task<VendorEventParticipationRules.EventCountSummary> LoadSummaryAsync(
        IAppDbContext db, Guid crewId, CancellationToken ct)
    {
        var rows = await db.EventAssignments
            .AsNoTracking()
            .Where(a => a.CrewId == crewId
                     && !a.IsDeleted
                     && !VendorEventParticipationRules.InactiveStatuses.Contains(a.Status)
                     && (a.ShiftId == null || db.EventShifts.Any(s => s.Id == a.ShiftId)))
            .Join(db.Events.AsNoTracking(),
                  a => a.EventId,
                  e => e.Id,
                  (a, e) => new { a.EventId, e.Status })
            .Distinct()
            .ToListAsync(ct);

        return VendorEventParticipationRules.SummarizeByStatus(
            rows.Select(r => (r.EventId, r.Status)));
    }
}
