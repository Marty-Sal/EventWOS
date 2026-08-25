using EventOpsOracle.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EventOpsOracle.Persistence.Configurations;

/// <summary>Maps <see cref="EventAnnouncement"/> to <c>event_announcements</c>.</summary>
public sealed class EventAnnouncementConfiguration : IEntityTypeConfiguration<EventAnnouncement>
{
    public void Configure(EntityTypeBuilder<EventAnnouncement> builder)
    {
        builder.ToTable("event_announcements");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(a => a.EventId).HasColumnName("event_id").IsRequired();
        builder.Property(a => a.Audience).HasColumnName("audience").HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(a => a.Subject).HasColumnName("subject").HasMaxLength(200).IsRequired();
        // No HasMaxLength — rich-text HTML mapped to Postgres' unbounded
        // `text`, same treatment as terms_and_conditions.content.
        builder.Property(a => a.BodyHtml).HasColumnName("body_html").IsRequired();
        builder.Property(a => a.RecipientCount).HasColumnName("recipient_count").HasDefaultValue(0);
        builder.Property(a => a.WhatsAppSentCount).HasColumnName("whatsapp_sent_count").HasDefaultValue(0);

        builder.Property(a => a.CreatedAt).HasColumnName("created_at");
        builder.Property(a => a.CreatedBy).HasColumnName("created_by");
        builder.Property(a => a.UpdatedAt).HasColumnName("updated_at");
        builder.Property(a => a.UpdatedBy).HasColumnName("updated_by");
        builder.Property(a => a.IsDeleted).HasColumnName("is_deleted").HasDefaultValue(false);
        builder.Property(a => a.DeletedAt).HasColumnName("deleted_at");
        builder.Property(a => a.DeletedBy).HasColumnName("deleted_by");

        // The database has this FK; EF did not know about it. Harmless today
        // (an announcement is always created against an event that already
        // exists) but mapped for the same reason as the attachment rows: the
        // model should describe the schema, not a subset of it.
        builder.HasOne<Event>()
               .WithMany()
               .HasForeignKey(a => a.EventId)
               .HasConstraintName("fk_event_announcements_event_id")
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(a => new { a.EventId, a.CreatedAt }).HasDatabaseName("ix_event_announcements_event_created");

        builder.HasQueryFilter(a => !a.IsDeleted);
    }
}
