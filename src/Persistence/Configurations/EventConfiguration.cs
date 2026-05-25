using EventWOS.Domain.Entities;
using EventWOS.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EventWOS.Persistence.Configurations;

public sealed class EventConfiguration : IEntityTypeConfiguration<Event>
{
    public void Configure(EntityTypeBuilder<Event> builder)
    {
        builder.ToTable("events");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id");

        builder.Property(e => e.Title)          .HasColumnName("title")             .HasMaxLength(200).IsRequired();
        builder.Property(e => e.Description)    .HasColumnName("description")       .HasMaxLength(2000);
        builder.Property(e => e.Venue)          .HasColumnName("venue")             .HasMaxLength(300).IsRequired();
        builder.Property(e => e.Address)        .HasColumnName("address")           .HasMaxLength(500);
        builder.Property(e => e.StartAt)        .HasColumnName("start_at");
        builder.Property(e => e.EndAt)          .HasColumnName("end_at");
        builder.Property(e => e.Status)         .HasColumnName("status")            .HasConversion<int>();
        builder.Property(e => e.MaxCrew)        .HasColumnName("max_crew")          .HasDefaultValue(0);
        builder.Property(e => e.CreatedByUserId).HasColumnName("created_by_user_id");

        // BaseEntity audit Guid (distinct from Creator nav property which uses CreatedByUserId FK)
        builder.Property(e => e.CreatedBy).HasColumnName("created_by");
        builder.Property(e => e.Notes)          .HasColumnName("notes")             .HasMaxLength(1000);

        // Audit
        builder.Property(e => e.CreatedAt) .HasColumnName("created_at");
        builder.Property(e => e.UpdatedAt) .HasColumnName("updated_at");
        builder.Property(e => e.UpdatedBy) .HasColumnName("updated_by");
        builder.Property(e => e.IsDeleted) .HasColumnName("is_deleted").HasDefaultValue(false);
        builder.Property(e => e.DeletedAt) .HasColumnName("deleted_at");
        builder.Property(e => e.DeletedBy) .HasColumnName("deleted_by");

        // Relationships
        builder.HasOne(e => e.Creator)
               .WithMany()
               .HasForeignKey(e => e.CreatedByUserId)
               .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(e => e.Assignments)
               .WithOne(a => a.Event)
               .HasForeignKey(a => a.EventId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => e.Status).HasDatabaseName("ix_events_status");
        builder.HasIndex(e => e.StartAt).HasDatabaseName("ix_events_start_at");
        builder.HasIndex(e => e.CreatedByUserId).HasDatabaseName("ix_events_created_by_user_id");
    }
}
