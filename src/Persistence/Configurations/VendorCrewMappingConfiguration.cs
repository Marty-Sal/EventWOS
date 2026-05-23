using EventWOS.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EventWOS.Persistence.Configurations;

public sealed class VendorCrewMappingConfiguration : IEntityTypeConfiguration<VendorCrewMapping>
{
    public void Configure(EntityTypeBuilder<VendorCrewMapping> builder)
    {
        builder.ToTable("vendor_crew_mappings");

        builder.HasKey(v => v.Id);
        builder.Property(v => v.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");

        builder.Property(v => v.VendorId).HasColumnName("vendor_id").IsRequired();
        builder.Property(v => v.CrewId).HasColumnName("crew_id").IsRequired();
        builder.Property(v => v.ApprovedByManagerId).HasColumnName("approved_by_manager_id").IsRequired();
        builder.Property(v => v.IsActive).HasColumnName("is_active").HasDefaultValue(true);
        builder.Property(v => v.MappedAt).HasColumnName("mapped_at").IsRequired();
        builder.Property(v => v.RemovedAt).HasColumnName("removed_at");
        builder.Property(v => v.Notes).HasColumnName("notes").HasMaxLength(500);

        // Audit
        builder.Property(v => v.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        builder.Property(v => v.CreatedBy).HasColumnName("created_by");
        builder.Property(v => v.UpdatedAt).HasColumnName("updated_at");
        builder.Property(v => v.UpdatedBy).HasColumnName("updated_by");
        builder.Property(v => v.IsDeleted).HasColumnName("is_deleted").HasDefaultValue(false);
        builder.Property(v => v.DeletedAt).HasColumnName("deleted_at");
        builder.Property(v => v.DeletedBy).HasColumnName("deleted_by");

        // ── Relationships ─────────────────────────────────────────────────────
        // EF cannot infer which FK maps to User.VendorMappings when there are
        // multiple FKs pointing back to the same table — so we configure all
        // three explicitly with no inverse navigation to avoid ambiguity.

        builder.HasOne(v => v.Vendor)
            .WithMany(u => u.VendorMappings)   // ← the ambiguous nav — resolved here
            .HasForeignKey(v => v.VendorId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(v => v.Crew)
            .WithMany()                         // no inverse on User for crew side
            .HasForeignKey(v => v.CrewId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(v => v.ApprovedBy)
            .WithMany()                         // no inverse on User for approver side
            .HasForeignKey(v => v.ApprovedByManagerId)
            .OnDelete(DeleteBehavior.Restrict);

        // Indexes
        builder.HasIndex(v => v.VendorId).HasDatabaseName("ix_vcm_vendor_id");
        builder.HasIndex(v => v.CrewId).HasDatabaseName("ix_vcm_crew_id");
        builder.HasIndex(v => new { v.VendorId, v.CrewId, v.IsActive })
            .HasDatabaseName("ix_vcm_vendor_crew_active");
    }
}
