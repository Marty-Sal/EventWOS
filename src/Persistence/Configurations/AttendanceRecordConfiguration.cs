using EventWOS.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EventWOS.Persistence.Configurations;

public sealed class AttendanceRecordConfiguration : IEntityTypeConfiguration<AttendanceRecord>
{
    public void Configure(EntityTypeBuilder<AttendanceRecord> builder)
    {
        builder.ToTable("attendance_records");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id");

        builder.Property(r => r.AssignmentId)    .HasColumnName("assignment_id");
        builder.Property(r => r.EventId)         .HasColumnName("event_id");
        builder.Property(r => r.CrewId)          .HasColumnName("crew_id");
        builder.Property(r => r.Action)          .HasColumnName("action").HasConversion<int>();
        builder.Property(r => r.RecordedAt)      .HasColumnName("recorded_at");
        // ── Location (split into two columns; see AttendanceRecord.cs) ─
        // The legacy "location" DB column still exists (idempotent
        // startup patch keeps it around for older rows written before
        // this split) but the domain no longer maps it — MSBuild would
        // fail if we tried to map two properties to the same column.
        // A one-shot migration copies any legacy values into the new
        // columns; see Program.cs Location-Split patch.
        builder.Property(r => r.LocationAddress).HasColumnName("location_address").HasMaxLength(200);
        builder.Property(r => r.LocationCoords) .HasColumnName("location_coords") .HasMaxLength(30);
        builder.Property(r => r.RecordedByUserId).HasColumnName("recorded_by_user_id").HasMaxLength(100);

        // Audit
        builder.Property(r => r.CreatedAt).HasColumnName("created_at");
        builder.Property(r => r.CreatedBy).HasColumnName("created_by");
        builder.Property(r => r.UpdatedAt).HasColumnName("updated_at");
        builder.Property(r => r.UpdatedBy).HasColumnName("updated_by");
        builder.Property(r => r.IsDeleted).HasColumnName("is_deleted").HasDefaultValue(false);
        builder.Property(r => r.DeletedAt).HasColumnName("deleted_at");
        builder.Property(r => r.DeletedBy).HasColumnName("deleted_by");

        // Relationships
        builder.HasOne(r => r.Assignment)
               .WithMany(a => a.AttendanceRecords)
               .HasForeignKey(r => r.AssignmentId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(r => r.Event)
               .WithMany()
               .HasForeignKey(r => r.EventId)
               .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(r => r.Crew)
               .WithMany()
               .HasForeignKey(r => r.CrewId)
               .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(r => r.AssignmentId).HasDatabaseName("ix_attendance_records_assignment_id");
        builder.HasIndex(r => r.EventId)     .HasDatabaseName("ix_attendance_records_event_id");
        builder.HasIndex(r => r.CrewId)      .HasDatabaseName("ix_attendance_records_crew_id");
        builder.HasIndex(r => r.RecordedAt)  .HasDatabaseName("ix_attendance_records_recorded_at");
    }
}
