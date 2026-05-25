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
        builder.Property(r => r.Location)        .HasColumnName("location").HasMaxLength(500);
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
