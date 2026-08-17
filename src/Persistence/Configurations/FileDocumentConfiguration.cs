using EventWOS.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EventWOS.Persistence.Configurations;

public sealed class FileDocumentConfiguration : IEntityTypeConfiguration<FileDocument>
{
    public void Configure(EntityTypeBuilder<FileDocument> b)
    {
        b.ToTable("file_documents");
        b.HasKey(f => f.Id);

        b.Property(f => f.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        b.Property(f => f.OwnerId).HasColumnName("owner_id").IsRequired();
        b.Property(f => f.EntityId).HasColumnName("entity_id");
        b.Property(f => f.DocumentType).HasColumnName("document_type").IsRequired();
        b.Property(f => f.StorageKey).HasColumnName("storage_key").HasMaxLength(500).IsRequired();
        b.Property(f => f.OriginalFileName).HasColumnName("original_file_name").HasMaxLength(255).IsRequired();
        b.Property(f => f.ContentType).HasColumnName("content_type").HasMaxLength(100).IsRequired();
        b.Property(f => f.FileSizeBytes).HasColumnName("file_size_bytes").IsRequired();
        b.Property(f => f.FileHash).HasColumnName("file_hash").HasMaxLength(64).IsRequired();
        b.Property(f => f.Provider).HasColumnName("provider").IsRequired();
        b.Property(f => f.ThumbnailStorageKey).HasColumnName("thumbnail_storage_key").HasMaxLength(500);

        // BaseEntity columns — UploadedAt/UploadedBy in the spec map onto CreatedAt/CreatedBy here.
        b.Property(f => f.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        b.Property(f => f.CreatedBy).HasColumnName("created_by");
        b.Property(f => f.UpdatedAt).HasColumnName("updated_at");
        b.Property(f => f.UpdatedBy).HasColumnName("updated_by");
        b.Property(f => f.IsDeleted).HasColumnName("is_deleted").HasDefaultValue(false);
        b.Property(f => f.DeletedAt).HasColumnName("deleted_at");
        b.Property(f => f.DeletedBy).HasColumnName("deleted_by");

        b.HasQueryFilter(f => !f.IsDeleted);

        // StorageKey is globally unique by construction (Guid-based) — enforce it so
        // a bug can never silently overwrite a different file's bytes.
        b.HasIndex(f => f.StorageKey).IsUnique().HasDatabaseName("ix_file_documents_storage_key");
        // "give me this user's profile photo / this user's ID proof" — the hottest read path.
        b.HasIndex(f => new { f.OwnerId, f.DocumentType }).HasDatabaseName("ix_file_documents_owner_type");
        // "give me all documents for this event" (EventDocument).
        b.HasIndex(f => f.EntityId).HasDatabaseName("ix_file_documents_entity");
    }
}
