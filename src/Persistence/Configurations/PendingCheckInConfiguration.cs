using EventWOS.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EventWOS.Persistence.Configurations;

/// <summary>
/// EF configuration for PendingCheckIn — the QR check-in handshake table.
/// Column naming follows the project's snake_case convention. Indexes cover
/// the three real query shapes:
///   1. Verify path — lookup by code (partial index over Pending status keeps
///      it lean; consumed/expired rows are cold data).
///   2. Crew's "my active QR" query — (assignment_id, status).
///   3. Sweeper — expires_at with a status filter.
/// </summary>
public sealed class PendingCheckInConfiguration : IEntityTypeConfiguration<PendingCheckIn>
{
    public void Configure(EntityTypeBuilder<PendingCheckIn> b)
    {
        b.ToTable("pending_checkins");
        b.HasKey(p => p.Id);

        b.Property(p => p.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        b.Property(p => p.AssignmentId).HasColumnName("assignment_id").IsRequired();
        b.Property(p => p.CrewId).HasColumnName("crew_id").IsRequired();
        b.Property(p => p.EventId).HasColumnName("event_id").IsRequired();
        b.Property(p => p.ShiftId).HasColumnName("shift_id");
        b.Property(p => p.Code).HasColumnName("code").HasMaxLength(32).IsRequired();
        // Crew's "lat,lng" at the moment they minted the QR. 40 chars is
        // plenty ("-12.345678,123.456789" = 21) but leaves headroom for
        // future precision changes or accuracy tags.
        b.Property(p => p.CrewLocation)
            .HasColumnName("crew_location").HasMaxLength(40).IsRequired();
        b.Property(p => p.ExpiresAt).HasColumnName("expires_at").IsRequired();
        b.Property(p => p.Status).HasColumnName("status").IsRequired();
        b.Property(p => p.ConsumedByVendorId).HasColumnName("consumed_by_vendor_id");
        b.Property(p => p.ConsumedAt).HasColumnName("consumed_at");

        // BaseEntity columns
        b.Property(p => p.CreatedAt).HasColumnName("created_at");
        b.Property(p => p.CreatedBy).HasColumnName("created_by");
        b.Property(p => p.UpdatedAt).HasColumnName("updated_at");
        b.Property(p => p.UpdatedBy).HasColumnName("updated_by");
        b.Property(p => p.IsDeleted).HasColumnName("is_deleted").HasDefaultValue(false);
        b.Property(p => p.DeletedAt).HasColumnName("deleted_at");
        b.Property(p => p.DeletedBy).HasColumnName("deleted_by");

        // Global soft-delete filter — matches every other entity in the project.
        b.HasQueryFilter(p => !p.IsDeleted);

        // Verify path: WHERE code = @code AND status = 0
        b.HasIndex(p => p.Code).HasDatabaseName("ix_pending_checkins_code");
        // Crew "my active" lookup
        b.HasIndex(p => new { p.AssignmentId, p.Status })
            .HasDatabaseName("ix_pending_checkins_assignment_status");
        // Sweeper
        b.HasIndex(p => p.ExpiresAt).HasDatabaseName("ix_pending_checkins_expires");
    }
}
