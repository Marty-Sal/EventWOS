using EventOpsOracle.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EventOpsOracle.Persistence.Configurations;

public sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("audit_logs");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(a => a.Action).HasColumnName("action").HasConversion<string>().HasMaxLength(100).IsRequired();
        builder.Property(a => a.PerformedByUserId).HasColumnName("performed_by_user_id");
        builder.Property(a => a.PerformedByIp).HasColumnName("performed_by_ip").HasMaxLength(45);
        builder.Property(a => a.EntityType).HasColumnName("entity_type").HasMaxLength(100).IsRequired();
        builder.Property(a => a.EntityId).HasColumnName("entity_id").HasMaxLength(100);
        // Stored as plain TEXT (JSON string produced by JsonSerializer.Serialize),
        // NOT a native jsonb column - a previous version of this config declared
        // HasColumnType("jsonb") which doesn't match the actual column type and
        // would fail every insert with "column is of type text but expression is
        // of type jsonb".
        builder.Property(a => a.OldValues).HasColumnName("old_values");
        builder.Property(a => a.NewValues).HasColumnName("new_values");
        builder.Property(a => a.AdditionalData).HasColumnName("additional_data").HasMaxLength(500);
        builder.Property(a => a.OccurredAt).HasColumnName("occurred_at").IsRequired();
        builder.Property(a => a.CreatedAt).HasColumnName("created_at");
        builder.Property(a => a.CreatedBy).HasColumnName("created_by");
        builder.Property(a => a.UpdatedAt).HasColumnName("updated_at");
        builder.Property(a => a.UpdatedBy).HasColumnName("updated_by");
        builder.Property(a => a.IsDeleted).HasColumnName("is_deleted").HasDefaultValue(false);
        builder.Property(a => a.DeletedAt).HasColumnName("deleted_at");
        builder.Property(a => a.DeletedBy).HasColumnName("deleted_by");

        // AuditLogs are append-only — never soft deleted in application code,
        // but IsDeleted/DeletedAt/DeletedBy still need real column mappings
        // above so EF's generated INSERT/UPDATE statements are valid.
        builder.HasIndex(a => a.PerformedByUserId).HasDatabaseName("ix_audit_logs_user");
        builder.HasIndex(a => a.OccurredAt).HasDatabaseName("ix_audit_logs_occurred_at");
        builder.HasIndex(a => new { a.EntityType, a.EntityId }).HasDatabaseName("ix_audit_logs_entity");


        // Global soft-delete filter — matches every other entity in the project.
        // List queries that need archived/deleted rows call IgnoreQueryFilters().
        builder.HasQueryFilter(a => !a.IsDeleted);
    }
}
