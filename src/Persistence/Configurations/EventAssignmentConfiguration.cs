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
        // ShiftId is nullable in the model during Phase B rollout; the DB
        // column flips to NOT NULL after backfill (see migration + Program.cs
        // schema patch). Leaving it nullable in the model is safe because
        // every code path that creates an assignment now goes through the
        // shift-aware ctor or AttachToShift().
        builder.Property(a => a.ShiftId)         .HasColumnName("shift_id");
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

        // Shift FK — RESTRICT because deleting a shift with active assignments
        // is a domain-level error caught upstream (EventShift.Archive() throws).
        // The FK is here for referential integrity in case raw SQL bypasses
        // the domain. WithMany() — EventShift doesn't expose an Assignments
        // collection to keep the entity surface small.
        builder.HasOne<EventShift>()
               .WithMany()
               .HasForeignKey(a => a.ShiftId)
               .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(a => a.ShiftId).HasDatabaseName("ix_event_assignments_shift_id");

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

        // Attendance audit note (admin overrides etc.)
        builder.Property(a => a.AttendanceNote)
               .HasColumnName("attendance_note")
               .HasMaxLength(500);
        builder.Property(a => a.AttendanceNoteAt)
               .HasColumnName("attendance_note_at");
        builder.Property(a => a.AttendanceNoteByUserId)
               .HasColumnName("attendance_note_by_user_id");
    }
}