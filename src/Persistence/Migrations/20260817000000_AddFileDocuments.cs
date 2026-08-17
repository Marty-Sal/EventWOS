using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventWOS.Persistence.Migrations
{
    /// <summary>
    /// File & Image Storage module — PostgreSQL holds ONLY metadata
    /// (storage_key points at bytes living in object storage / local disk,
    /// never in this table). See Domain.Entities.FileDocument.
    ///
    /// Idempotent raw SQL, matching this project's existing migration style.
    /// </summary>
    [Migration("20260817000000_AddFileDocuments")]
    public partial class AddFileDocuments : Migration
    {
        protected override void Up(MigrationBuilder mb)
        {
            mb.Sql(@"
CREATE TABLE IF NOT EXISTS file_documents (
    id                    UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
    owner_id              UUID NOT NULL,
    entity_id             UUID,
    document_type         INTEGER NOT NULL,
    storage_key           VARCHAR(500) NOT NULL,
    original_file_name    VARCHAR(255) NOT NULL,
    content_type          VARCHAR(100) NOT NULL,
    file_size_bytes        BIGINT NOT NULL,
    file_hash             VARCHAR(64) NOT NULL,
    provider              INTEGER NOT NULL,
    thumbnail_storage_key VARCHAR(500),
    created_at            TIMESTAMPTZ NOT NULL DEFAULT now(),
    created_by            UUID,
    updated_at            TIMESTAMPTZ,
    updated_by            UUID,
    is_deleted            BOOLEAN NOT NULL DEFAULT false,
    deleted_at            TIMESTAMPTZ,
    deleted_by            UUID
);

CREATE UNIQUE INDEX IF NOT EXISTS ix_file_documents_storage_key
    ON file_documents (storage_key);

CREATE INDEX IF NOT EXISTS ix_file_documents_owner_type
    ON file_documents (owner_id, document_type);

CREATE INDEX IF NOT EXISTS ix_file_documents_entity
    ON file_documents (entity_id);
");
        }

        protected override void Down(MigrationBuilder mb)
        {
            mb.Sql("DROP TABLE IF EXISTS file_documents;");
        }
    }
}
