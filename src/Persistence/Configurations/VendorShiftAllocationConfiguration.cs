using EventWOS.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EventWOS.Persistence.Configurations;

/// <summary>
/// Maps <see cref="VendorShiftAllocation"/> to <c>vendor_shift_allocations</c>.
///
/// FK semantics:
///   • ShiftId  → event_shifts(id) ON DELETE CASCADE
///     (When a shift is hard-deleted, its allocations go too — they're
///     meaningless without the shift. Soft-delete is the normal path.)
///   • VendorId → users(id)        ON DELETE RESTRICT
///     (Cannot delete a user that still has live allocations referencing
///     them; force the admin to archive first. Users are soft-deleted in
///     practice anyway.)
///
/// Unique index `ux_vendor_shift_allocations_shift_vendor_active`:
///   Filtered on `is_deleted = false`. A vendor gets at most ONE active
///   allocation per shift; archived rows free the slot so re-adding the
///   same vendor (after a mistaken archive) works. Same pattern as the
///   scope_of_work name index (rule #25 / rule #1).
///
/// Index `ix_vendor_shift_allocations_vendor_id` — drives "all shifts
/// vendor X is allocated to" for the vendor portal (Phase C "my shifts"
/// view, Phase D event listing).
///
/// Soft-delete query filter matches the rest of the codebase.
/// </summary>
public sealed class VendorShiftAllocationConfiguration
    : IEntityTypeConfiguration<VendorShiftAllocation>
{
    public void Configure(EntityTypeBuilder<VendorShiftAllocation> b)
    {
        b.ToTable("vendor_shift_allocations");
        b.HasKey(a => a.Id);

        b.Property(a => a.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        b.Property(a => a.ShiftId).HasColumnName("shift_id");
        b.Property(a => a.VendorId).HasColumnName("vendor_id");
        b.Property(a => a.Quota).HasColumnName("quota");
        b.Property(a => a.CreatedByUserId).HasColumnName("created_by_user_id");

        b.Property(a => a.CreatedAt).HasColumnName("created_at");
        b.Property(a => a.CreatedBy).HasColumnName("created_by");
        b.Property(a => a.UpdatedAt).HasColumnName("updated_at");
        b.Property(a => a.UpdatedBy).HasColumnName("updated_by");
        b.Property(a => a.IsDeleted).HasColumnName("is_deleted").HasDefaultValue(false);
        b.Property(a => a.DeletedAt).HasColumnName("deleted_at");
        b.Property(a => a.DeletedBy).HasColumnName("deleted_by");

        b.HasOne(a => a.Shift)
         .WithMany()  // EventShift doesn't need a back-collection — we always
                     // query allocations by shift_id directly, never via nav.
         .HasForeignKey(a => a.ShiftId)
         .OnDelete(DeleteBehavior.Cascade);

        b.HasOne(a => a.Vendor)
         .WithMany()
         .HasForeignKey(a => a.VendorId)
         .OnDelete(DeleteBehavior.Restrict);

        // Filtered unique — only active rows count toward uniqueness so
        // archive + re-create works cleanly. Same shape as scope_of_work.
        b.HasIndex(a => new { a.ShiftId, a.VendorId })
         .HasDatabaseName("ux_vendor_shift_allocations_shift_vendor_active")
         .HasFilter("is_deleted = false")
         .IsUnique();

        b.HasIndex(a => a.VendorId).HasDatabaseName("ix_vendor_shift_allocations_vendor_id");

        b.HasQueryFilter(a => !a.IsDeleted);
    }
}
