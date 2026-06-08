using EventWOS.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EventWOS.Persistence.Configurations;

/// <summary>
/// Maps <see cref="EventShift"/> to the <c>event_shifts</c> table.
///
/// FK semantics:
///   • EventId         → events(id)         ON DELETE CASCADE
///     (When an event is hard-deleted, its shifts go too. Soft-delete is the
///     normal path; cascade matters only for tests / admin tooling.)
///   • ScopeOfWorkId   → scope_of_work(id)  ON DELETE RESTRICT
///     (Cannot delete a scope row that any shift depends on. The catalog
///     uses soft-delete + archive — restoring an archived scope keeps the
///     historical reference valid, hence RESTRICT not CASCADE.)
///
/// Indexes:
///   • ix_event_shifts_event_id        — drives "all shifts for event X"
///   • ix_event_shifts_scope_of_work_id — drives "what events use scope Y"
///     (Phase A's archive flow needs this so we can check shift dependencies
///     in O(log n).)
///
/// Soft-delete query filter mirrors every other entity in the codebase.
/// </summary>
public sealed class EventShiftConfiguration : IEntityTypeConfiguration<EventShift>
{
    public void Configure(EntityTypeBuilder<EventShift> b)
    {
        b.ToTable("event_shifts");
        b.HasKey(s => s.Id);

        b.Property(s => s.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        b.Property(s => s.EventId).HasColumnName("event_id");
        b.Property(s => s.ScopeOfWorkId).HasColumnName("scope_of_work_id");
        b.Property(s => s.CrewCount).HasColumnName("crew_count");
        b.Property(s => s.StartAt).HasColumnName("start_at");
        b.Property(s => s.EndAt).HasColumnName("end_at");
        b.Property(s => s.CreatedByUserId).HasColumnName("created_by_user_id");

        b.Property(s => s.CreatedAt).HasColumnName("created_at");
        b.Property(s => s.CreatedBy).HasColumnName("created_by");
        b.Property(s => s.UpdatedAt).HasColumnName("updated_at");
        b.Property(s => s.UpdatedBy).HasColumnName("updated_by");
        b.Property(s => s.IsDeleted).HasColumnName("is_deleted").HasDefaultValue(false);
        b.Property(s => s.DeletedAt).HasColumnName("deleted_at");
        b.Property(s => s.DeletedBy).HasColumnName("deleted_by");

        b.HasOne(s => s.Event)
         .WithMany(e => e.Shifts)
         .HasForeignKey(s => s.EventId)
         .OnDelete(DeleteBehavior.Cascade);

        b.HasOne(s => s.ScopeOfWork)
         .WithMany()
         .HasForeignKey(s => s.ScopeOfWorkId)
         .OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(s => s.EventId).HasDatabaseName("ix_event_shifts_event_id");
        b.HasIndex(s => s.ScopeOfWorkId).HasDatabaseName("ix_event_shifts_scope_of_work_id");

        b.HasQueryFilter(s => !s.IsDeleted);
    }
}
