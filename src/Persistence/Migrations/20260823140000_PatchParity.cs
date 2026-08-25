using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventOpsOracle.Persistence.Migrations
{
    /// <summary>
    /// Brings the migration set up to parity with emergencySchemaPatchSql.
    ///
    /// The two had silently diverged. Schema-diffing a migrations-only database
    /// against a migrations-plus-patch database (both built live, then compared
    /// through information_schema) showed the patch was carrying schema of its
    /// own that no migration had:
    ///
    ///   attendance_records.location_address / location_coords  (Phase F split)
    ///   crew_payments.updated_by / deleted_at / deleted_by     (audit columns)
    ///   payroll_batches.updated_by / deleted_at / deleted_by   (audit columns)
    ///   pending_checkins.crew_location                         (Phase G)
    ///   ix_refresh_tokens_user_id / ix_user_sessions_user_active
    ///
    /// and that the patch also RELAXES two constraints the migrations impose:
    /// crew_payments.updated_at and payroll_batches.updated_at are created
    /// NOT NULL DEFAULT now() by migration, while the entities leave UpdatedAt
    /// null until the row is first modified. The patch drops both the NOT NULL
    /// and the default; without that a fresh migrations-only database rejects
    /// payment and payroll inserts.
    ///
    /// Consequence before this migration: a brand-new environment built purely
    /// from migrations came up MISSING columns the running code reads and writes.
    /// The boot patch was load-bearing, not a safety net -- which is exactly why
    /// it could not simply be retired.
    ///
    /// With this applied the migration set is a true superset of the patch, so
    /// Program.cs skips the patch on a fully migrated database. Every statement
    /// is guarded: a no-op on production (where the patch already created all of
    /// it) and effective on fresh installs.
    /// </summary>
    [Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute(typeof(AppDbContext))]
    [Migration("20260823140000_PatchParity")]
    public partial class PatchParity : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DO $$
BEGIN
    -- --- attendance_records: the Phase F location split ---------------------
    -- Human-readable place plus 'lat,lng' for the map link. The check-in path
    -- writes both, so a database without them fails every check-in.
    ALTER TABLE attendance_records ADD COLUMN IF NOT EXISTS location_address VARCHAR(200);
    ALTER TABLE attendance_records ADD COLUMN IF NOT EXISTS location_coords  VARCHAR(30);

    -- --- payroll audit columns ----------------------------------------------
    ALTER TABLE crew_payments   ADD COLUMN IF NOT EXISTS updated_by UUID;
    ALTER TABLE crew_payments   ADD COLUMN IF NOT EXISTS deleted_at TIMESTAMPTZ;
    ALTER TABLE crew_payments   ADD COLUMN IF NOT EXISTS deleted_by UUID;
    ALTER TABLE payroll_batches ADD COLUMN IF NOT EXISTS updated_by UUID;
    ALTER TABLE payroll_batches ADD COLUMN IF NOT EXISTS deleted_at TIMESTAMPTZ;
    ALTER TABLE payroll_batches ADD COLUMN IF NOT EXISTS deleted_by UUID;

    -- --- updated_at must be nullable on both payroll tables -----------------
    -- ALTER COLUMN has no IF NOT EXISTS form, but dropping an absent NOT NULL
    -- or DEFAULT is a no-op, so these are idempotent by nature.
    ALTER TABLE crew_payments   ALTER COLUMN updated_at DROP NOT NULL;
    ALTER TABLE crew_payments   ALTER COLUMN updated_at DROP DEFAULT;
    ALTER TABLE payroll_batches ALTER COLUMN updated_at DROP NOT NULL;
    ALTER TABLE payroll_batches ALTER COLUMN updated_at DROP DEFAULT;

    -- --- pending_checkins.crew_location (Phase G) ---------------------------
    -- NOT NULL with an empty-string default: existing rows predate the column
    -- and have no crew-side fix to backfill.
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_name = 'pending_checkins' AND column_name = 'crew_location'
    ) THEN
        ALTER TABLE pending_checkins ADD COLUMN crew_location VARCHAR(40) NOT NULL DEFAULT '';
    END IF;

    -- --- hot-path indexes the patch had and migrations lacked ---------------
    CREATE INDEX IF NOT EXISTS ix_refresh_tokens_user_id    ON refresh_tokens(user_id);
    CREATE INDEX IF NOT EXISTS ix_user_sessions_user_active ON user_sessions(user_id, is_active);
END $$;
");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately empty. Every statement above is additive or relaxes a
            // constraint the running code depends on; reversing any of it would
            // break a working database rather than restore a good state.
        }
    }
}
