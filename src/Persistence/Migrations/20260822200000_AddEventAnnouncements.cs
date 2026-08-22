using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventWOS.Persistence.Migrations
{
    /// <summary>
    /// Event notifications module: Admin/Manager broadcasts a rich-text
    /// message (plus optional attachments) to an event's vendors and/or crew.
    ///
    /// Three tables:
    ///   event_announcements             — the message itself (HTML body, audience, delivery counts)
    ///   event_announcement_attachments  — join to file_documents (bytes stay in object storage)
    ///   event_announcement_reads        — per-user read receipts, drives the unread badge
    ///
    /// Idempotent raw SQL, matching this project's migration style.
    /// </summary>
    [Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute(typeof(AppDbContext))]
    [Migration("20260822200000_AddEventAnnouncements")]
    public partial class AddEventAnnouncements : Migration
    {
        protected override void Up(MigrationBuilder mb)
        {
            mb.Sql(@"
CREATE TABLE IF NOT EXISTS event_announcements (
    id                   UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
    event_id             UUID NOT NULL,
    audience             VARCHAR(20) NOT NULL,
    subject              VARCHAR(200) NOT NULL,
    body_html            TEXT NOT NULL,
    recipient_count      INT NOT NULL DEFAULT 0,
    whatsapp_sent_count  INT NOT NULL DEFAULT 0,
    created_at           TIMESTAMPTZ NOT NULL DEFAULT now(),
    created_by           UUID,
    updated_at           TIMESTAMPTZ,
    updated_by           UUID,
    is_deleted           BOOLEAN NOT NULL DEFAULT false,
    deleted_at           TIMESTAMPTZ,
    deleted_by           UUID
);

CREATE INDEX IF NOT EXISTS ix_event_announcements_event_created
    ON event_announcements (event_id, created_at);

CREATE TABLE IF NOT EXISTS event_announcement_attachments (
    id                UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
    announcement_id   UUID NOT NULL,
    file_document_id  UUID NOT NULL,
    created_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
    created_by        UUID,
    updated_at        TIMESTAMPTZ,
    updated_by        UUID,
    is_deleted        BOOLEAN NOT NULL DEFAULT false,
    deleted_at        TIMESTAMPTZ,
    deleted_by        UUID
);

CREATE INDEX IF NOT EXISTS ix_announcement_attachments_announcement
    ON event_announcement_attachments (announcement_id);

CREATE UNIQUE INDEX IF NOT EXISTS ux_announcement_attachments_pair
    ON event_announcement_attachments (announcement_id, file_document_id);

CREATE TABLE IF NOT EXISTS event_announcement_reads (
    id                UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
    announcement_id   UUID NOT NULL,
    user_id           UUID NOT NULL,
    read_at           TIMESTAMPTZ NOT NULL DEFAULT now(),
    created_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
    created_by        UUID,
    updated_at        TIMESTAMPTZ,
    updated_by        UUID,
    is_deleted        BOOLEAN NOT NULL DEFAULT false,
    deleted_at        TIMESTAMPTZ,
    deleted_by        UUID
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_announcement_reads_pair
    ON event_announcement_reads (announcement_id, user_id);

CREATE INDEX IF NOT EXISTS ix_announcement_reads_user
    ON event_announcement_reads (user_id);

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE constraint_name = 'fk_event_announcements_event_id'
    ) THEN
        ALTER TABLE event_announcements
            ADD CONSTRAINT fk_event_announcements_event_id
            FOREIGN KEY (event_id) REFERENCES events(id) ON DELETE CASCADE;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE constraint_name = 'fk_announcement_attachments_announcement_id'
    ) THEN
        ALTER TABLE event_announcement_attachments
            ADD CONSTRAINT fk_announcement_attachments_announcement_id
            FOREIGN KEY (announcement_id) REFERENCES event_announcements(id) ON DELETE CASCADE;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE constraint_name = 'fk_announcement_attachments_file_id'
    ) THEN
        ALTER TABLE event_announcement_attachments
            ADD CONSTRAINT fk_announcement_attachments_file_id
            FOREIGN KEY (file_document_id) REFERENCES file_documents(id) ON DELETE CASCADE;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE constraint_name = 'fk_announcement_reads_announcement_id'
    ) THEN
        ALTER TABLE event_announcement_reads
            ADD CONSTRAINT fk_announcement_reads_announcement_id
            FOREIGN KEY (announcement_id) REFERENCES event_announcements(id) ON DELETE CASCADE;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE constraint_name = 'fk_announcement_reads_user_id'
    ) THEN
        ALTER TABLE event_announcement_reads
            ADD CONSTRAINT fk_announcement_reads_user_id
            FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE;
    END IF;
END $$;
");
        }

        protected override void Down(MigrationBuilder mb)
        {
            mb.Sql(@"
                DROP TABLE IF EXISTS event_announcement_reads;
                DROP TABLE IF EXISTS event_announcement_attachments;
                DROP TABLE IF EXISTS event_announcements;
            ");
        }
    }
}
