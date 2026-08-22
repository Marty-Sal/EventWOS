using EventWOS.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EventWOS.Persistence.Configurations;

/// <summary>Maps <see cref="EventAnnouncementAttachment"/> to <c>event_announcement_attachments</c>.</summary>
public sealed class EventAnnouncementAttachmentConfiguration : IEntityTypeConfiguration<EventAnnouncementAttachment>
{
    public void Configure(EntityTypeBuilder<EventAnnouncementAttachment> builder)
    {
        builder.ToTable("event_announcement_attachments");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(a => a.AnnouncementId).HasColumnName("announcement_id").IsRequired();
        builder.Property(a => a.FileDocumentId).HasColumnName("file_document_id").IsRequired();

        builder.Property(a => a.CreatedAt).HasColumnName("created_at");
        builder.Property(a => a.CreatedBy).HasColumnName("created_by");
        builder.Property(a => a.UpdatedAt).HasColumnName("updated_at");
        builder.Property(a => a.UpdatedBy).HasColumnName("updated_by");
        builder.Property(a => a.IsDeleted).HasColumnName("is_deleted").HasDefaultValue(false);
        builder.Property(a => a.DeletedAt).HasColumnName("deleted_at");
        builder.Property(a => a.DeletedBy).HasColumnName("deleted_by");

        builder.HasIndex(a => a.AnnouncementId).HasDatabaseName("ix_announcement_attachments_announcement");
        builder.HasIndex(a => new { a.AnnouncementId, a.FileDocumentId })
               .IsUnique().HasDatabaseName("ux_announcement_attachments_pair");

        builder.HasQueryFilter(a => !a.IsDeleted);
    }
}
