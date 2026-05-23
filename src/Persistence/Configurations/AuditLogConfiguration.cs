using EventWOS.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EventWOS.Persistence.Configurations;

public sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("audit_logs");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(a => a.Action).HasColumnName("action").IsRequired();
        builder.Property(a => a.PerformedByUserId).HasColumnName("performed_by_user_id");
        builder.Property(a => a.PerformedByIp).HasColumnName("performed_by_ip").HasMaxLength(45);
        builder.Property(a => a.EntityType).HasColumnName("entity_type").HasMaxLength(100).IsRequired();
        builder.Property(a => a.EntityId).HasColumnName("entity_id").HasMaxLength(50);
        builder.Property(a => a.OldValues).HasColumnName("old_values").HasColumnType("jsonb");
        builder.Property(a => a.NewValues).HasColumnName("new_values").HasColumnType("jsonb");
        builder.Property(a => a.AdditionalData).HasColumnName("additional_data").HasMaxLength(500);
        builder.Property(a => a.OccurredAt).HasColumnName("occurred_at").IsRequired();
        builder.Property(a => a.CreatedAt).HasColumnName("created_at");

        // AuditLogs are append-only — never soft deleted
        builder.HasIndex(a => a.PerformedByUserId).HasDatabaseName("ix_audit_logs_user");
        builder.HasIndex(a => a.OccurredAt).HasDatabaseName("ix_audit_logs_occurred_at");
        builder.HasIndex(a => new { a.EntityType, a.EntityId }).HasDatabaseName("ix_audit_logs_entity");
    }
}
