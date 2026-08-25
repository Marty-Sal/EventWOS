using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventOpsOracle.Persistence.Migrations;

/// <summary>
/// Adds device_registrations: one row per browser or installed PWA that has
/// agreed to receive push notifications.
///
/// Push is the only channel whose destination is a set rather than a value -- a
/// user's subscriptions come and go without them logging in -- so the Push
/// NotificationDelivery row addresses the user and the sender fans out across
/// this table at send time.
///
/// Unique indexes are filtered on is_deleted: the endpoint is the identity of a
/// subscription, and an unfiltered unique index would let one soft-deleted
/// tombstone lock that browser out of subscribing again for good.
/// </summary>
public partial class AddDeviceRegistrations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE IF NOT EXISTS device_registrations (
                id                    UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
                user_id               UUID NOT NULL,
                provider              VARCHAR(20) NOT NULL,
                endpoint              VARCHAR(500),
                p256dh_key            VARCHAR(200),
                auth_secret           VARCHAR(100),
                push_token            VARCHAR(500),
                device_id             VARCHAR(100),
                platform              VARCHAR(40),
                user_agent            VARCHAR(400),
                is_active             BOOLEAN NOT NULL DEFAULT true,
                last_seen_at          TIMESTAMPTZ,
                last_success_at       TIMESTAMPTZ,
                deactivated_at        TIMESTAMPTZ,
                deactivation_reason   VARCHAR(200),
                consecutive_failures  INT NOT NULL DEFAULT 0,
                created_at            TIMESTAMPTZ NOT NULL DEFAULT now(),
                created_by            UUID,
                updated_at            TIMESTAMPTZ,
                updated_by            UUID,
                is_deleted            BOOLEAN NOT NULL DEFAULT false,
                deleted_at            TIMESTAMPTZ,
                deleted_by            UUID,
                CONSTRAINT fk_device_registrations_user_id
                    FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ux_device_registrations_endpoint
                ON device_registrations (endpoint) WHERE is_deleted = false;

            CREATE UNIQUE INDEX IF NOT EXISTS ux_device_registrations_push_token
                ON device_registrations (push_token) WHERE is_deleted = false;

            CREATE INDEX IF NOT EXISTS ix_device_registrations_user_active
                ON device_registrations (user_id, is_active);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TABLE IF EXISTS device_registrations;");
    }
}
