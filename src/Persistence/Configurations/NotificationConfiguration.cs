using EventWOS.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EventWOS.Persistence.Configurations;

/// <summary>Maps <see cref="Notification"/> to <c>notifications</c>.</summary>
public sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("notifications");
        builder.HasKey(n => n.Id);

        builder.Property(n => n.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(n => n.RecipientUserId).HasColumnName("recipient_user_id").IsRequired();
        builder.Property(n => n.EventId).HasColumnName("event_id");
        builder.Property(n => n.ActorUserId).HasColumnName("actor_user_id");
        builder.Property(n => n.TemplateCode).HasColumnName("template_code").HasMaxLength(60).IsRequired();
        builder.Property(n => n.Priority).HasColumnName("priority").HasConversion<string>().HasMaxLength(10).IsRequired();
        builder.Property(n => n.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(15).IsRequired();

        // Real jsonb: the payload is a JSON object and admins will want to query
        // into it ("which notifications mentioned this shift"). Kept as a string
        // in the domain so the Domain layer stays free of Npgsql types.
        builder.Property(n => n.DataJson).HasColumnName("data").HasColumnType("jsonb").IsRequired();

        builder.Property(n => n.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(200).IsRequired();
        builder.Property(n => n.CorrelationId).HasColumnName("correlation_id").HasMaxLength(100);
        builder.Property(n => n.ReadAt).HasColumnName("read_at");

        builder.Property(n => n.CreatedAt).HasColumnName("created_at");
        builder.Property(n => n.CreatedBy).HasColumnName("created_by");
        builder.Property(n => n.UpdatedAt).HasColumnName("updated_at");
        builder.Property(n => n.UpdatedBy).HasColumnName("updated_by");
        builder.Property(n => n.IsDeleted).HasColumnName("is_deleted").HasDefaultValue(false);
        builder.Property(n => n.DeletedAt).HasColumnName("deleted_at");
        builder.Property(n => n.DeletedBy).HasColumnName("deleted_by");

        // THE idempotency guard. A double-clicked Assign button, a retried API
        // call and a replayed outbox row all land on the same key, and the
        // database -- not application code -- is what makes the second one lose.
        builder.HasIndex(n => n.IdempotencyKey)
               .IsUnique()
               .HasDatabaseName("ux_notifications_idempotency_key");

        // The recipient's notification list, newest first.
        builder.HasIndex(n => new { n.RecipientUserId, n.CreatedAt })
               .HasDatabaseName("ix_notifications_recipient_created");

        builder.HasIndex(n => n.EventId).HasDatabaseName("ix_notifications_event");

        // Explicit collection navigation per project convention: the parent owns
        // its deliveries and cascades, since a delivery is meaningless alone.
        builder.HasMany(n => n.Deliveries)
               .WithOne()
               .HasForeignKey(d => d.NotificationId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.Metadata.FindNavigation(nameof(Notification.Deliveries))!
               .SetPropertyAccessMode(PropertyAccessMode.Field);

        builder.HasQueryFilter(n => !n.IsDeleted);
    }
}
