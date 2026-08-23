using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventWOS.Persistence.Migrations
{
    /// <summary>
    /// Notification platform, phase 1: durable notification state in Postgres.
    ///
    /// Four tables:
    ///   notifications           - one logical notification per recipient, with the
    ///                             resolved placeholder data as jsonb
    ///   notification_deliveries - one row per channel, each with independent state,
    ///                             attempt count and retry schedule
    ///   notification_templates  - the wording, per code+channel+language, versioned
    ///   outbox_messages         - transactional outbox; written in the same
    ///                             SaveChanges as the business data
    ///
    /// Postgres is the source of truth on purpose: no Redis, no broker. A business
    /// transaction commits its notification work atomically, and a background worker
    /// performs the provider calls afterwards, so a provider outage can never roll
    /// back an assignment and a crash can never silently drop a message.
    ///
    /// Index notes:
    ///   ix_notification_deliveries_claim is the hot path (the worker's claim query,
    ///   ordered by priority then due time), which is why priority is denormalised
    ///   onto the delivery row rather than joined from notifications.
    ///   ux_notifications_idempotency_key is what actually prevents duplicate
    ///   messages - application-level checks alone still race.
    ///
    /// Idempotent raw SQL, matching this project's migration style. Mirrored in
    /// emergencySchemaPatchSql in Program.cs, because the startup migration gate
    /// means a migration alone never reaches production.
    /// </summary>
    [Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute(typeof(AppDbContext))]
    [Migration("20260823190000_AddNotificationPlatform")]
    public partial class AddNotificationPlatform : Migration
    {
        protected override void Up(MigrationBuilder mb)
        {
            mb.Sql(@"
CREATE TABLE IF NOT EXISTS notifications (
    id                 UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
    recipient_user_id  UUID NOT NULL,
    event_id           UUID,
    actor_user_id      UUID,
    template_code      VARCHAR(60) NOT NULL,
    priority           VARCHAR(10) NOT NULL,
    status             VARCHAR(15) NOT NULL,
    data               JSONB NOT NULL DEFAULT '{}'::jsonb,
    idempotency_key    VARCHAR(200) NOT NULL,
    correlation_id     VARCHAR(100),
    read_at            TIMESTAMPTZ,
    created_at         TIMESTAMPTZ NOT NULL DEFAULT now(),
    created_by         UUID,
    updated_at         TIMESTAMPTZ,
    updated_by         UUID,
    is_deleted         BOOLEAN NOT NULL DEFAULT false,
    deleted_at         TIMESTAMPTZ,
    deleted_by         UUID
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_notifications_idempotency_key
    ON notifications (idempotency_key);
CREATE INDEX IF NOT EXISTS ix_notifications_recipient_created
    ON notifications (recipient_user_id, created_at);
CREATE INDEX IF NOT EXISTS ix_notifications_event
    ON notifications (event_id);

CREATE TABLE IF NOT EXISTS notification_deliveries (
    id                          UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
    notification_id             UUID NOT NULL,
    channel                     VARCHAR(15) NOT NULL,
    destination                 VARCHAR(320),
    provider                    VARCHAR(40) NOT NULL,
    template_version            INT NOT NULL DEFAULT 1,
    priority                    VARCHAR(10) NOT NULL,
    status                      VARCHAR(15) NOT NULL,
    provider_message_id         VARCHAR(200),
    provider_response_reference VARCHAR(200),
    attempt_count               INT NOT NULL DEFAULT 0,
    last_attempt_at             TIMESTAMPTZ,
    next_attempt_at             TIMESTAMPTZ,
    accepted_at                 TIMESTAMPTZ,
    delivered_at                TIMESTAMPTZ,
    read_at                     TIMESTAMPTZ,
    failed_at                   TIMESTAMPTZ,
    failure_reason              VARCHAR(500),
    locked_by                   VARCHAR(100),
    locked_at                   TIMESTAMPTZ,
    created_at                  TIMESTAMPTZ NOT NULL DEFAULT now(),
    created_by                  UUID,
    updated_at                  TIMESTAMPTZ,
    updated_by                  UUID,
    is_deleted                  BOOLEAN NOT NULL DEFAULT false,
    deleted_at                  TIMESTAMPTZ,
    deleted_by                  UUID,
    CONSTRAINT fk_notification_deliveries_notification
        FOREIGN KEY (notification_id) REFERENCES notifications (id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS ix_notification_deliveries_claim
    ON notification_deliveries (status, priority, next_attempt_at);
CREATE INDEX IF NOT EXISTS ix_notification_deliveries_provider_message
    ON notification_deliveries (provider_message_id);
CREATE INDEX IF NOT EXISTS ix_notification_deliveries_notification
    ON notification_deliveries (notification_id);
CREATE UNIQUE INDEX IF NOT EXISTS ux_notification_deliveries_notification_channel
    ON notification_deliveries (notification_id, channel);

CREATE TABLE IF NOT EXISTS notification_templates (
    id                    UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
    code                  VARCHAR(60) NOT NULL,
    channel               VARCHAR(15) NOT NULL,
    language              VARCHAR(10) NOT NULL DEFAULT 'en',
    subject               VARCHAR(300),
    body                  TEXT NOT NULL,
    provider_template_id  VARCHAR(200),
    version               INT NOT NULL DEFAULT 1,
    is_active             BOOLEAN NOT NULL DEFAULT true,
    created_at            TIMESTAMPTZ NOT NULL DEFAULT now(),
    created_by            UUID,
    updated_at            TIMESTAMPTZ,
    updated_by            UUID,
    is_deleted            BOOLEAN NOT NULL DEFAULT false,
    deleted_at            TIMESTAMPTZ,
    deleted_by            UUID
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_notification_templates_code_channel_lang
    ON notification_templates (code, channel, language);

CREATE TABLE IF NOT EXISTS outbox_messages (
    id              UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
    aggregate_type  VARCHAR(60) NOT NULL,
    aggregate_id    UUID,
    message_type    VARCHAR(60) NOT NULL,
    payload         JSONB NOT NULL,
    status          VARCHAR(15) NOT NULL,
    attempt_count   INT NOT NULL DEFAULT 0,
    available_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
    locked_at       TIMESTAMPTZ,
    locked_by       VARCHAR(100),
    processed_at    TIMESTAMPTZ,
    last_error      VARCHAR(1000),
    correlation_id  VARCHAR(100),
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    created_by      UUID,
    updated_at      TIMESTAMPTZ,
    updated_by      UUID,
    is_deleted      BOOLEAN NOT NULL DEFAULT false,
    deleted_at      TIMESTAMPTZ,
    deleted_by      UUID
);

CREATE INDEX IF NOT EXISTS ix_outbox_messages_status_available
    ON outbox_messages (status, available_at);
CREATE INDEX IF NOT EXISTS ix_outbox_messages_status_locked
    ON outbox_messages (status, locked_at);
");
        }

        protected override void Down(MigrationBuilder mb)
        {
            // Deliberately drops deliveries before notifications so the FK does
            // not block, and leaves nothing behind: this is the whole subsystem.
            mb.Sql(@"
DROP TABLE IF EXISTS notification_deliveries;
DROP TABLE IF EXISTS notifications;
DROP TABLE IF EXISTS notification_templates;
DROP TABLE IF EXISTS outbox_messages;
");
        }
    }
}
