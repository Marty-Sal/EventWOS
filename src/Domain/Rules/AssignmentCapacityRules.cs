using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using EventOpsOracle.Domain.Entities;
using EventOpsOracle.Domain.Enums;

namespace EventOpsOracle.Domain.Rules;

/// <summary>
/// Centralised rules for counting how many real "seats" of an event are
/// currently occupied. Used by:
///   * AssignCrewCommand (manager / admin assign)
///   * VendorAssignCrewCommand (vendor self-assign)
///   * GetEventByIdQuery / GetEventsQuery / GetMyEventsQuery (AssignedCrew display)
///
/// A row occupies a seat iff:
///   1. It is not soft-deleted.
///   2. It has a real crew member attached (CrewId != null — placeholder
///      vendor-anchor rows do NOT count).
///   3. Its Status represents an active or completed assignment (i.e. it
///      is NOT Declined / RejectedByVendor / RejectedByManager / NoShow).
///
/// Keep this in sync with EventAssignment lifecycle: any time you add a
/// new "inactive" terminal status, list it in NonOccupyingStatuses below.
/// </summary>
public static class AssignmentCapacityRules
{
    /// <summary>Statuses that should be treated as freeing a seat back to the pool.</summary>
    public static readonly AssignmentStatus[] NonOccupyingStatuses =
    {
        AssignmentStatus.Declined,
        AssignmentStatus.RejectedByVendor,
        AssignmentStatus.RejectedByManager,
        AssignmentStatus.NoShow,
    };

    /// <summary>EF-translatable predicate: does this assignment occupy a seat?</summary>
    public static Expression<Func<EventAssignment, bool>> OccupiesSeat => a =>
        !a.IsDeleted
        && a.CrewId != null
        && a.Status != AssignmentStatus.Declined
        && a.Status != AssignmentStatus.RejectedByVendor
        && a.Status != AssignmentStatus.RejectedByManager
        && a.Status != AssignmentStatus.NoShow;

    /// <summary>
    /// Phase D step 21: confirmed-only predicate. "How many crew on this
    /// event are actually approved and ready to show up?" Excludes
    /// everything still in the pipeline (Invited / VendorApproved /
    /// PendingManagerApproval) so the admin Events card can show
    /// "2/40 crew" meaning "2 fully approved out of 40 needed", not
    /// "8/40 invited (most still in review)".
    ///
    /// Confirmed = ManagerApproved (post-vendor admin approval) OR
    ///             Confirmed (legacy explicit step, if used)        OR
    ///             Attended (already showed up — implies confirmed).
    /// </summary>
    public static Expression<Func<EventAssignment, bool>> IsConfirmed => a =>
        !a.IsDeleted
        && a.CrewId != null
        && (a.Status == AssignmentStatus.ManagerApproved
         || a.Status == AssignmentStatus.Confirmed
         || a.Status == AssignmentStatus.Attended);

    // ── Phase B (Scope-of-Work shifts) ───────────────────────────────────────
    //
    // Shift-aware occupancy predicate. Same semantics as OccupiesSeat — but
    // pinned to a specific shift. Used by:
    //   • EventShift.Update      (handler computes shift's seat count
    //                             before letting CrewCount shrink)
    //   • EventShift.Archive     (handler checks 0 active before deleting)
    //   • Phase C vendor-quota   (counting "how many slots in shift X
    //                             does vendor Y currently occupy?")
    //
    // We keep two predicates rather than one parameterised one because EF
    // Core's Expression<Func<…>> rewriting is far happier with closed
    // expressions than open ones. Tiny duplication, big query-plan win.

    /// <summary>
    /// EF-translatable predicate: does this assignment occupy a seat on
    /// the given shift? Pass the result to <c>.Count()</c> against any
    /// IQueryable&lt;EventAssignment&gt;.
    /// </summary>
    public static Expression<Func<EventAssignment, bool>> OccupiesSeatOnShift(Guid shiftId) =>
        a => a.ShiftId == shiftId
          && !a.IsDeleted
          && a.CrewId != null
          && a.Status != AssignmentStatus.Declined
          && a.Status != AssignmentStatus.RejectedByVendor
          && a.Status != AssignmentStatus.RejectedByManager
          && a.Status != AssignmentStatus.NoShow;

    /// <summary>
    /// How many of a shift's seats are actually committed, for capacity gates
    /// and every "N free" display.
    ///
    /// The subtlety this exists to fix: a vendor-only invite drops ONE placeholder
    /// anchor row (CrewId == null) on the shift and grants a VendorShiftAllocation
    /// quota. The anchor is kept forever on purpose -- it is what keeps the event
    /// visible in the vendor's My Events after their crew are rejected -- so the
    /// vendor's seats are described TWICE: once by the quota, and again by the
    /// anchor plus each crew member they place. Charging both means a capacity-3
    /// shift with one vendor (quota 2, 2 crew placed) reports 3 seats gone and
    /// claims to be full while the Vendor Quotas panel correctly shows 1 free.
    ///
    /// The rule: a vendor's QUOTA is their seat reservation, and the crew they
    /// place fill it. Anchors are never charged on top.
    ///
    ///   * vendor WITH an allocation -> max(quota, crew they have placed).
    ///     The max covers a shift shrunk below what a vendor already staffed.
    ///   * vendor WITHOUT an allocation (legacy invites, before quotas existed)
    ///     -> their rows are counted one-for-one, anchors included, because
    ///     nothing else describes the seats they hold. This is what still stops
    ///     placeholders being stacked past capacity on un-quota'd shifts.
    ///   * crew with no vendor at all (direct assignment) -> one seat each.
    /// </summary>
    /// <param name="allocations">Active (non-archived) allocations on the shift.</param>
    /// <param name="activeRows">
    /// Assignment rows on the shift that are not soft-deleted and not in a
    /// seat-freeing status -- i.e. the rows ReservesSeatOnShift would return.
    /// </param>
    public static int CommittedSeatsOnShift(
        IEnumerable<(Guid VendorId, int Quota)> allocations,
        IEnumerable<(Guid? VendorId, bool IsPlaceholder)> activeRows)
    {
        var quotaByVendor = allocations
            .GroupBy(a => a.VendorId)
            .ToDictionary(g => g.Key, g => g.Sum(a => a.Quota));

        var rows = activeRows.ToList();

        var committed = 0;

        foreach (var (vendorId, quota) in quotaByVendor)
        {
            var placed = rows.Count(r => !r.IsPlaceholder && r.VendorId == vendorId);
            committed += Math.Max(quota, placed);
        }

        // Everything not covered by a quota above: direct crew, and rows under a
        // vendor who has no allocation on this shift.
        committed += rows.Count(r => r.VendorId is null || !quotaByVendor.ContainsKey(r.VendorId.Value));

        return committed;
    }

    /// <summary>
    /// EF-translatable predicate: does this assignment reserve a seat on
    /// the given shift? Same status rules as <see cref="OccupiesSeatOnShift"/>
    /// BUT also counts placeholder rows (CrewId == null) created by
    /// vendor-only invites. Use this for shift-level CAPACITY checks —
    /// otherwise admins can over-invite a shift by stacking placeholders
    /// (each placeholder reserves a real seat the vendor will fill later).
    ///
    /// Rejected / declined / no-show rows are excluded because they've
    /// freed their seat back to the pool.
    /// </summary>
    public static Expression<Func<EventAssignment, bool>> ReservesSeatOnShift(Guid shiftId) =>
        a => a.ShiftId == shiftId
          && !a.IsDeleted
          && a.Status != AssignmentStatus.Declined
          && a.Status != AssignmentStatus.RejectedByVendor
          && a.Status != AssignmentStatus.RejectedByManager
          && a.Status != AssignmentStatus.NoShow;
}
