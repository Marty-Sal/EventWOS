using EventWOS.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EventWOS.Persistence.Configurations;

/// <summary>Maps <see cref="OutboxMessage"/> to <c>outbox_messages</c>.</summary>
public sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_messages");
        builder.HasKey(o => o.Id);

        builder.Property(o => o.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(o => o.AggregateType).HasColumnName("aggregate_type").HasMaxLength(60).IsRequired();
        builder.Property(o => o.AggregateId).HasColumnName("aggregate_id");
        builder.Property(o => o.MessageType).HasColumnName("message_type").HasMaxLength(60).IsRequired();
        builder.Property(o => o.PayloadJson).HasColumnName("payload").HasColumnType("jsonb").IsRequired();
        builder.Property(o => o.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(15).IsRequired();
        builder.Property(o => o.AttemptCount).HasColumnName("attempt_count").HasDefaultValue(0);
        builder.Property(o => o.AvailableAt).HasColumnName("available_at").IsRequired();
        builder.Property(o => o.LockedAt).HasColumnName("locked_at");
        builder.Property(o => o.LockedBy).HasColumnName("locked_by").HasMaxLength(100);
        builder.Property(o => o.ProcessedAt).HasColumnName("processed_at");
        builder.Property(o => o.LastError).HasColumnName("last_error").HasMaxLength(1000);
        builder.Property(o => o.CorrelationId).HasColumnName("correlation_id").HasMaxLength(100);

        builder.Property(o => o.CreatedAt).HasColumnName("created_at");
        builder.Property(o => o.CreatedBy).HasColumnName("created_by");
        builder.Property(o => o.UpdatedAt).HasColumnName("updated_at");
        builder.Property(o => o.UpdatedBy).HasColumnName("updated_by");
        builder.Property(o => o.IsDeleted).HasColumnName("is_deleted").HasDefaultValue(false);
        builder.Property(o => o.DeletedAt).HasColumnName("deleted_at");
        builder.Property(o => o.DeletedBy).HasColumnName("deleted_by");

        // The dispatcher's claim query (FOR UPDATE SKIP LOCKED runs behind this).
        builder.HasIndex(o => new { o.Status, o.AvailableAt })
               .HasDatabaseName("ix_outbox_messages_status_available");

        // Crash recovery sweep: rows stuck in Processing past the lock timeout.
        builder.HasIndex(o => new { o.Status, o.LockedAt })
               .HasDatabaseName("ix_outbox_messages_status_locked");

        // NOTE: no soft-delete query filter. The outbox is infrastructure, not
        // business data -- a filtered-out row would be a message nobody ever
        // sends and nobody can see. Retention is handled by a purge of
        // Processed rows instead.
    }
}
