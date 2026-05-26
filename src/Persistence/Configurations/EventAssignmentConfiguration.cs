using EventWOS.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EventWOS.Persistence.Configurations;

public sealed class EventAssignmentConfiguration : IEntityTypeConfiguration<EventAssignment>
{
    public void Configure(EntityTypeBuilder<EventAssignment> builder)
    {
        builder.ToTable("event_assignments");

        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).HasColumnName("id");

        builder.Property(a => a.EventId)         .HasColumnName("event_id");
        builder.Property(a => a.CrewId)          .HasColumnName("crew_id").IsRequired(false);
        builder.Property(a => a.VendorId)        .HasColumnName("vendor_id").IsRequired(false);
        builder.Property(a => a.AssignedByUserId).HasColumnName("assigned_by_user_id");
        builder.Property(a => a.Status)          .HasColumnName("status").HasConversion<int>();
        builder.Property(a => a.Notes)           .HasColumnName("notes").HasMaxLength(1000);
        builder.Property(a => a.ConfirmedAt)       .HasColumnName("confirmed_at");
        builder.Property(a => a.DeclinedAt)        .HasColumnName("declined_at");
        builder.Property(a => a.CrewRespondedAt)   .HasColumnName("crew_responded_at");
        builder.Property(a => a.VendorReviewedAt)  .HasColumnName("vendor_reviewed_at");
        builder.Property(a => a.ManagerReviewedAt) .HasColumnName("manager_reviewed_at");
        builder.Property(a => a.RejectionReason)   .HasColumnName("rejection_reason").HasMaxLength(2000);
        builder.Property(a => a.RejectedByUserId)  .HasColumnName("rejected_by_user_id");

        // Audit
        builder.Property(a => a.CreatedAt).HasColumnName("created_at");
        builder.Property(a => a.CreatedBy).HasColumnName("created_by");
        builder.Property(a => a.UpdatedAt).HasColumnName("updated_at");
        builder.Property(a => a.UpdatedBy).HasColumnName("updated_by");
        builder.Property(a => a.IsDeleted).HasColumnName("is_deleted").HasDefaultValue(false);
        builder.Property(a => a.DeletedAt).HasColumnName("deleted_at");
        builder.Property(a => a.DeletedBy).HasColumnName("deleted_by");

        // Prevent duplicate active assignment (crew can only be assigned once per event)
        builder.HasIndex(a => new { a.EventId, a.CrewId })
               .IsUnique()
               .HasFilter("is_deleted = false")
               .HasDatabaseName("ix_event_assignments_event_crew_unique");

        // Relationships
        builder.HasOne(a => a.Event)
               .WithMany(e => e.Assignments)
               .HasForeignKey(a => a.EventId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(a => a.Crew)
               .WithMany()
               .HasForeignKey(a => a.CrewId)
               .IsRequired(false)
               .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(a => a.Vendor)
               .WithMany()
               .HasForeignKey(a => a.VendorId)
               .IsRequired(false)
               .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(a => a.AssignedBy)
               .WithMany()
               .HasForeignKey(a => a.AssignedByUserId)
               .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(a => a.AttendanceRecords)
               .WithOne(r => r.Assignment)
               .HasForeignKey(r => r.AssignmentId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(a => a.EventId) .HasDatabaseName("ix_event_assignments_event_id");
        builder.HasIndex(a => a.CrewId)  .HasDatabaseName("ix_event_assignments_crew_id");
        builder.HasIndex(a => a.VendorId).HasDatabaseName("ix_event_assignments_vendor_id");
        builder.Property(a => a.VendorRating).HasColumnName("vendor_rating").HasColumnType("numeric(3,1)");
        builder.Property(a => a.RatedAt).HasColumnName("rated_at");
    }
}