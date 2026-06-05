using EventWOS.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EventWOS.Persistence.Configurations;

public sealed class CrewGroupMemberConfiguration : IEntityTypeConfiguration<CrewGroupMember>
{
    public void Configure(EntityTypeBuilder<CrewGroupMember> builder)
    {
        builder.ToTable("crew_group_members");
        builder.HasKey(m => m.Id);

        builder.Property(m => m.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(m => m.CrewGroupId).HasColumnName("crew_group_id").IsRequired();
        builder.Property(m => m.CrewId).HasColumnName("crew_id").IsRequired();
        builder.Property(m => m.AddedAt).HasColumnName("added_at").IsRequired();

        builder.Property(m => m.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        builder.Property(m => m.CreatedBy).HasColumnName("created_by");
        builder.Property(m => m.UpdatedAt).HasColumnName("updated_at");
        builder.Property(m => m.UpdatedBy).HasColumnName("updated_by");
        builder.Property(m => m.IsDeleted).HasColumnName("is_deleted").HasDefaultValue(false);
        builder.Property(m => m.DeletedAt).HasColumnName("deleted_at");
        builder.Property(m => m.DeletedBy).HasColumnName("deleted_by");

        // Group nav configured on the CrewGroup side (HasMany(...).WithOne).
        builder.HasOne(m => m.Crew)
            .WithMany()
            .HasForeignKey(m => m.CrewId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(m => m.CrewGroupId).HasDatabaseName("ix_cgm_crew_group_id");
        builder.HasIndex(m => m.CrewId).HasDatabaseName("ix_cgm_crew_id");

        // Filtered unique index so the SAME (group, crew) pair can be soft-deleted
        // and re-added later. Postgres partial index syntax.
        builder.HasIndex(m => new { m.CrewGroupId, m.CrewId })
            .HasDatabaseName("ux_cgm_group_crew_active")
            .IsUnique()
            .HasFilter("is_deleted = false");
    }
}
