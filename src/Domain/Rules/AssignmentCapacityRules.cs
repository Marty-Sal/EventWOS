using System;
using System.Linq.Expressions;
using EventWOS.Domain.Entities;
using EventWOS.Domain.Enums;

namespace EventWOS.Domain.Rules;

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
}
