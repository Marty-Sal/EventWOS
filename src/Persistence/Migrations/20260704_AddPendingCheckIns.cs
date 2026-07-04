using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventWOS.Persistence.Migrations
{
    /// <summary>
    /// QR-verified check-in handshake table.
    ///
    /// Crew clicks "Check In" → server mints a pending_checkins row with a
    /// short opaque code and 10-min TTL. Vendor scans the QR on their phone,
    /// which POSTs the code back and flips the row to Consumed (and writes
    /// the actual attendance_records row via the existing handler).
    ///
    /// Idempotent raw SQL. Also grants the Vendor role a new
    /// attendance:verify permission — see step 27's DatabaseSeeder update
    /// for the C# equivalent that runs on every startup, but seeding the
    /// permission row here means the seeder never has to insert a new
    /// permission (it upserts role-grants only).
    /// </summary>
    [Migration("20260704000000_AddPendingCheckIns")]
    public partial class AddPendingCheckIns : Migration
    {
        protected override void Up(MigrationBuilder mb)
        {
            mb.Sql(@"
CREATE TABLE IF NOT EXISTS pending_checkins (
    id                    UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
    assignment_id         UUID NOT NULL,
    crew_id               UUID NOT NULL,
    event_id              UUID NOT NULL,
    shift_id              UUID,
    code                  VARCHAR(32) NOT NULL,
    expires_at            TIMESTAMPTZ NOT NULL,
    status                INTEGER NOT NULL DEFAULT 0,
    consumed_by_vendor_id UUID,
    consumed_at           TIMESTAMPTZ,
    created_at            TIMESTAMPTZ NOT NULL DEFAULT now(),
    created_by            UUID,
    updated_at            TIMESTAMPTZ,
    updated_by            UUID,
    is_deleted            BOOLEAN NOT NULL DEFAULT false,
    deleted_at            TIMESTAMPTZ,
    deleted_by            UUID
);

-- Verify path (WHERE code = @code): plain btree is fine — codes are
-- ~72-bit entropy, so cardinality on a live table stays very high.
CREATE INDEX IF NOT EXISTS ix_pending_checkins_code
    ON pending_checkins (code);

-- Crew's ""my active QR"" lookup + fast lookup of a live Pending row
-- when regenerating (we auto-cancel the prior row).
CREATE INDEX IF NOT EXISTS ix_pending_checkins_assignment_status
    ON pending_checkins (assignment_id, status);

-- Sweeper index — bg task picks up rows with status=0 AND expires_at < now.
CREATE INDEX IF NOT EXISTS ix_pending_checkins_expires
    ON pending_checkins (expires_at);

-- Seed the new permission so DatabaseSeeder's role-grant upsert can bind
-- it to the Vendor + Admin/Manager roles on next startup. INSERT ... ON
-- CONFLICT DO NOTHING keeps the migration idempotent.
INSERT INTO permissions (id, name, resource, action, description, created_at)
SELECT gen_random_uuid(),
       'attendance:verify',
       'attendance',
       'verify',
       'Scan a crew QR to verify their check-in',
       now()
WHERE NOT EXISTS (
    SELECT 1 FROM permissions
    WHERE name = 'attendance:verify' AND is_deleted = false
);
");
        }

        protected override void Down(MigrationBuilder mb)
        {
            mb.Sql(@"
DROP TABLE IF EXISTS pending_checkins;
DELETE FROM permissions WHERE name = 'attendance:verify';
");
        }
    }
}
