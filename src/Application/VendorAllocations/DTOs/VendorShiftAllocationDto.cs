namespace EventWOS.Application.VendorAllocations.DTOs;

/// <summary>
/// Read-side shape for a per-vendor quota on a specific shift.
///
/// <see cref="CurrentlyAssigned"/> is computed at query time — number of
/// crew this vendor currently has occupying a seat on this shift, using
/// the standard <c>OccupiesSeatOnShift</c> predicate. Surfaces directly
/// in the admin allocation table as "<c>{CurrentlyAssigned} / {Quota}</c>".
///
/// <see cref="VendorName"/> and <see cref="ScopeName"/> are denormalised
/// for display purposes so the admin UI doesn't have to do an N+1 lookup.
/// </summary>
public sealed record VendorShiftAllocationDto(
    Guid     Id,
    Guid     ShiftId,
    Guid     VendorId,
    string   VendorName,
    Guid     EventId,
    Guid     ScopeOfWorkId,
    string   ScopeName,
    int      Quota,
    int      CurrentlyAssigned,
    bool     IsArchived,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    // Phase D step 18: invite-status of the placeholder row that anchors
    // this vendor to this shift. Null when no placeholder row exists
    // (extremely-legacy allocations without an assignment). Otherwise
    // mirrors EventAssignment.Status of the most-recent placeholder
    // (CrewId == null) for this (event, vendor) pair: "Invited",
    // "VendorAccepted", "RejectedByVendor", "Declined", etc. The Manager
    // UI surfaces this as "Accepted" / "Pending Invite" badges in the
    // Vendor Quotas panel so admins can see who has confirmed without
    // scrolling through the full Crew Assignments list.
    string?  InviteStatus = null
);
