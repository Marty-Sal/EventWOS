using EventWOS.Domain.Common;

namespace EventWOS.Domain.Entities;

/// <summary>
/// Per-vendor staffing quota on a specific <see cref="EventShift"/>.
/// Phase C of the Scope-of-Work feature.
///
/// Replaces the old "vendor can invite anyone to any event" model. Now an
/// Admin/Manager explicitly grants each Vendor a quota on each shift
/// they need to staff. Example for "City Marathon, Sat 6pm":
///   • Box Office shift (12 crew):  Vendor A → 8 quota, Vendor B → 4 quota
///   • Gates shift     ( 6 crew):  Vendor B → 6 quota
///
/// Capacity rules (all enforced at handler + DB layer):
///   • SUM(allocations.quota WHERE shift_id = X) must NEVER exceed
///     EventShift.CrewCount on that shift. Handler validates before save.
///   • Vendor's per-shift OccupiesSeat count must NEVER exceed their quota
///     on that shift. <see cref="Rules.AssignmentCapacityRules"/> teaches
///     the predicate this dimension.
///   • Re-invites, rejections, and vendor-revokes release quota back to
///     the right (shift, vendor) cell — handled implicitly because
///     OccupiesSeat filters by status, not by row existence.
///
/// Unique on (ShiftId, VendorId) — a vendor gets ONE allocation per shift,
/// not a stack of them. Use <see cref="UpdateQuota"/> to grow/shrink.
///
/// Soft-deletable. Archiving while crew are already assigned under this
/// allocation throws (same belt-and-braces pattern as EventShift.Archive).
/// </summary>
public sealed class VendorShiftAllocation : BaseEntity
{
    private VendorShiftAllocation() { }

    public VendorShiftAllocation(
        Guid shiftId,
        Guid vendorId,
        int  quota,
        Guid createdByUserId)
    {
        if (shiftId  == Guid.Empty) throw new ArgumentException("ShiftId is required.",  nameof(shiftId));
        if (vendorId == Guid.Empty) throw new ArgumentException("VendorId is required.", nameof(vendorId));
        if (quota    <  1)
            throw new ArgumentException("Quota must be at least 1.", nameof(quota));

        ShiftId         = shiftId;
        VendorId        = vendorId;
        Quota           = quota;
        CreatedByUserId = createdByUserId;
    }

    public Guid ShiftId         { get; private set; }
    public Guid VendorId        { get; private set; }
    public int  Quota           { get; private set; }
    public Guid CreatedByUserId { get; private set; }

    // Navigation
    public EventShift? Shift  { get; private set; }
    public User?       Vendor { get; private set; }

    /// <summary>
    /// Change the quota. Shrinking below seats already occupied by this
    /// vendor on this shift is rejected — mirrors EventShift.Update's floor
    /// guard. Caller (handler) supplies the current occupied count.
    /// </summary>
    public void UpdateQuota(int newQuota, int currentSeatsOccupied)
    {
        if (newQuota < 1)
            throw new ArgumentException("Quota must be at least 1.", nameof(newQuota));
        if (newQuota < currentSeatsOccupied)
            throw new InvalidOperationException(
                $"Cannot reduce quota below {currentSeatsOccupied} — that many crew are already approved or confirmed under this vendor on this shift.");

        Quota     = newQuota;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Soft-delete the allocation. Throws if the vendor has any crew
    /// currently occupying a seat on this shift — they'd be orphaned.
    /// Idempotent: archiving an already-archived allocation is a no-op.
    /// </summary>
    public void Archive(Guid deletedByUserId, int currentSeatsOccupied)
    {
        if (IsDeleted) return;
        if (currentSeatsOccupied > 0)
            throw new InvalidOperationException(
                $"Cannot archive allocation — {currentSeatsOccupied} crew are already approved or confirmed under this vendor on this shift.");

        IsDeleted = true;
        DeletedAt = DateTime.UtcNow;
        DeletedBy = deletedByUserId;
    }
}
