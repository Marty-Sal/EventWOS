using EventWOS.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EventWOS.Persistence.Configurations;

/// <summary>Maps <see cref="EventAnnouncementRead"/> to <c>event_announcement_reads</c>.</summary>
public sealed class EventAnnouncementReadConfiguration : IEntityTypeConfiguration<EventAnnouncementRead>
{
    public void Configure(EntityTypeBuilder<EventAnnouncementRead> builder)
    {
        builder.ToTable("event_announcement_reads");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(r => r.AnnouncementId).HasColumnName("announcement_id").IsRequired();
        builder.Property(r => r.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(r => r.ReadAt).HasColumnName("read_at").IsRequired();

        builder.Property(r => r.CreatedAt).HasColumnName("created_at");
        builder.Property(r => r.CreatedBy).HasColumnName("created_by");
        builder.Property(r => r.UpdatedAt).HasColumnName("updated_at");
        builder.Property(r => r.UpdatedBy).HasColumnName("updated_by");
        builder.Property(r => r.IsDeleted).HasColumnName("is_deleted").HasDefaultValue(false);
        builder.Property(r => r.DeletedAt).HasColumnName("deleted_at");
        builder.Property(r => r.DeletedBy).HasColumnName("deleted_by");

        // One read-receipt per (announcement, user) — the mark-read endpoint
        // is idempotent and relies on this.
        builder.HasIndex(r => new { r.AnnouncementId, r.UserId })
               .IsUnique().HasDatabaseName("ux_announcement_reads_pair");
        builder.HasIndex(r => r.UserId).HasDatabaseName("ix_announcement_reads_user");

        // Mapped for the same reason as the attachments above: EF must know the
        // dependency exists, even though neither side exposes a navigation.
        builder.HasOne<EventAnnouncement>()
               .WithMany()
               .HasForeignKey(r => r.AnnouncementId)
               .HasConstraintName("fk_announcement_reads_announcement_id")
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<User>()
               .WithMany()
               .HasForeignKey(r => r.UserId)
               .HasConstraintName("fk_announcement_reads_user_id")
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasQueryFilter(r => !r.IsDeleted);
    }
}
