using EventOpsOracle.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EventOpsOracle.Persistence.Configurations;

/// <summary>Maps <see cref="NotificationDelivery"/> to <c>notification_deliveries</c>.</summary>
public sealed class NotificationDeliveryConfiguration : IEntityTypeConfiguration<NotificationDelivery>
{
    public void Configure(EntityTypeBuilder<NotificationDelivery> builder)
    {
        builder.ToTable("notification_deliveries");
        builder.HasKey(d => d.Id);

        builder.Property(d => d.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(d => d.NotificationId).HasColumnName("notification_id").IsRequired();
        builder.Property(d => d.Channel).HasColumnName("channel").HasConversion<string>().HasMaxLength(15).IsRequired();
        builder.Property(d => d.Destination).HasColumnName("destination").HasMaxLength(320);
        builder.Property(d => d.Provider).HasColumnName("provider").HasMaxLength(40).IsRequired();
        builder.Property(d => d.TemplateVersion).HasColumnName("template_version").HasDefaultValue(1);
        builder.Property(d => d.Priority).HasColumnName("priority").HasConversion<string>().HasMaxLength(10).IsRequired();
        builder.Property(d => d.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(15).IsRequired();

        builder.Property(d => d.ProviderMessageId).HasColumnName("provider_message_id").HasMaxLength(200);
        builder.Property(d => d.ProviderResponseReference).HasColumnName("provider_response_reference").HasMaxLength(200);

        builder.Property(d => d.AttemptCount).HasColumnName("attempt_count").HasDefaultValue(0);
        builder.Property(d => d.LastAttemptAt).HasColumnName("last_attempt_at");
        builder.Property(d => d.NextAttemptAt).HasColumnName("next_attempt_at");
        builder.Property(d => d.AcceptedAt).HasColumnName("accepted_at");
        builder.Property(d => d.DeliveredAt).HasColumnName("delivered_at");
        builder.Property(d => d.ReadAt).HasColumnName("read_at");
        builder.Property(d => d.FailedAt).HasColumnName("failed_at");
        builder.Property(d => d.FailureReason).HasColumnName("failure_reason").HasMaxLength(500);
        builder.Property(d => d.LockedBy).HasColumnName("locked_by").HasMaxLength(100);
        builder.Property(d => d.LockedAt).HasColumnName("locked_at");

        builder.Property(d => d.CreatedAt).HasColumnName("created_at");
        builder.Property(d => d.CreatedBy).HasColumnName("created_by");
        builder.Property(d => d.UpdatedAt).HasColumnName("updated_at");
        builder.Property(d => d.UpdatedBy).HasColumnName("updated_by");
        builder.Property(d => d.IsDeleted).HasColumnName("is_deleted").HasDefaultValue(false);
        builder.Property(d => d.DeletedAt).HasColumnName("deleted_at");
        builder.Property(d => d.DeletedBy).HasColumnName("deleted_by");

        // The worker's claim query: pending rows that are due, best priority
        // first, oldest first within a priority. This index is the hot path of
        // the whole subsystem, hence priority living on this table.
        builder.HasIndex(d => new { d.Status, d.Priority, d.NextAttemptAt })
               .HasDatabaseName("ix_notification_deliveries_claim");

        // Webhook correlation. AiSensy and SES both identify a message only by
        // their own id, so this lookup happens on every inbound status event.
        builder.HasIndex(d => d.ProviderMessageId)
               .HasDatabaseName("ix_notification_deliveries_provider_message");

        builder.HasIndex(d => d.NotificationId)
               .HasDatabaseName("ix_notification_deliveries_notification");

        // One delivery per channel per notification: makes a duplicate fan-out
        // impossible at the database level even if an outbox row is processed twice.
        builder.HasIndex(d => new { d.NotificationId, d.Channel })
               .IsUnique()
               .HasDatabaseName("ux_notification_deliveries_notification_channel");

        builder.HasQueryFilter(d => !d.IsDeleted);
    }
}
