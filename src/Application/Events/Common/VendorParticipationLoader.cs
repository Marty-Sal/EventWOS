using EventOpsOracle.Application.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace EventOpsOracle.Application.Events.Common;

/// <summary>
/// Loads the raw (vendor, event, assignment status, event status) rows that
/// <see cref="VendorEventParticipationRules"/> reasons over.
///
/// Kept separate from the rules themselves so the rules stay pure and unit
/// testable without a DbContext. The SQL side narrows only by vendor and by
/// the shared InactiveStatuses list; deciding what "done" means is left to
/// the rules so there is exactly one place that knows.
/// </summary>
public static class VendorParticipationLoader
{
    public static async Task<List<VendorEventParticipationRules.ParticipationRow>> LoadAsync(
        IAppDbContext db, IReadOnlyCollection<Guid> vendorIds, CancellationToken ct)
    {
        if (vendorIds.Count == 0)
            return new List<VendorEventParticipationRules.ParticipationRow>();

        // Projected to an anonymous type first, then mapped: this codebase
        // deliberately avoids leaning on EF to materialise custom structs.
        var raw = await db.EventAssignments
            .AsNoTracking()
            .Where(a => a.VendorId != null
                     && vendorIds.Contains(a.VendorId.Value)
                     && !a.IsDeleted
                     && !VendorEventParticipationRules.InactiveStatuses.Contains(a.Status)
                     // Deleted shift -> nothing to staff; keep the dashboard tiles and
                     // My Events counting the same events.
                     && (a.ShiftId == null || db.EventShifts.Any(s => s.Id == a.ShiftId)))
            .Join(db.Events.AsNoTracking(),
                  a => a.EventId,
                  e => e.Id,
                  (a, e) => new
                  {
                      VendorId        = a.VendorId!.Value,
                      a.EventId,
                      AssignmentStatus = a.Status,
                      EventStatus      = e.Status
                  })
            .ToListAsync(ct);

        return raw
            .Select(r => new VendorEventParticipationRules.ParticipationRow(
                r.VendorId, r.EventId, r.AssignmentStatus, r.EventStatus))
            .ToList();
    }

    /// <summary>Convenience wrapper for the single-vendor read paths.</summary>
    public static async Task<int> CountEventsDoneAsync(
        IAppDbContext db, Guid vendorId, CancellationToken ct)
        => VendorEventParticipationRules.CountEventsDone(
               await LoadAsync(db, new[] { vendorId }, ct));

    /// <summary>Live/upcoming/completed breakdown for a single vendor's own events.</summary>
    public static async Task<VendorEventParticipationRules.EventCountSummary> LoadSummaryAsync(
        IAppDbContext db, Guid vendorId, CancellationToken ct)
        => VendorEventParticipationRules.Summarize(
               await LoadAsync(db, new[] { vendorId }, ct));
}
