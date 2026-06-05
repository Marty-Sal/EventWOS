using EventWOS.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EventWOS.Persistence.Configurations;

public sealed class CrewGroupConfiguration : IEntityTypeConfiguration<CrewGroup>
{
    public void Configure(EntityTypeBuilder<CrewGroup> builder)
    {
        builder.ToTable("crew_groups");
        builder.HasKey(g => g.Id);

        builder.Property(g => g.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(g => g.VendorId).HasColumnName("vendor_id").IsRequired();
        builder.Property(g => g.Name).HasColumnName("name").HasMaxLength(120).IsRequired();
        builder.Property(g => g.Description).HasColumnName("description").HasMaxLength(500);

        builder.Property(g => g.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        builder.Property(g => g.CreatedBy).HasColumnName("created_by");
        builder.Property(g => g.UpdatedAt).HasColumnName("updated_at");
        builder.Property(g => g.UpdatedBy).HasColumnName("updated_by");
        builder.Property(g => g.IsDeleted).HasColumnName("is_deleted").HasDefaultValue(false);
        builder.Property(g => g.DeletedAt).HasColumnName("deleted_at");
        builder.Property(g => g.DeletedBy).HasColumnName("deleted_by");

        // Vendor → Groups (no inverse collection on User to keep User clean).
        builder.HasOne(g => g.Vendor)
            .WithMany()
            .HasForeignKey(g => g.VendorId)
            .OnDelete(DeleteBehavior.Restrict);

        // Explicit configuration of the collection navigation per active
        // instruction (rule #9): prevents EF runtime validation crashes.
        builder.HasMany(g => g.Members)
            .WithOne(m => m.CrewGroup)
            .HasForeignKey(m => m.CrewGroupId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(g => g.VendorId).HasDatabaseName("ix_crew_groups_vendor_id");
        builder.HasIndex(g => new { g.VendorId, g.Name })
            .HasDatabaseName("ix_crew_groups_vendor_name");
    }
}
