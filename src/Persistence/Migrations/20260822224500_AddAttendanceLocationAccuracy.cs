using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventWOS.Persistence.Migrations
{
    /// <summary>
    /// Records how good each attendance GPS fix actually was.
    ///
    /// attendance_records.location_accuracy_meters
    /// pending_checkins.crew_location_accuracy_meters
    ///
    /// Why this is worth a column: coordinates conceal their own quality. A 10 m
    /// GPS fix and a 2 km cell-tower estimate are both stored as six decimal
    /// places and both render as an equally confident pin. Until now an auditor
    /// reviewing a disputed shift had no way to distinguish "stood at the gate"
    /// from "was somewhere in the district", and once geofencing starts rejecting
    /// people, "the system said you were 400 m away" is only defensible if the
    /// fix's own margin of error is on record next to it.
    ///
    /// Both columns are NULLABLE and stay null for:
    ///   * every existing row (no backfill is possible — the information was
    ///     never captured, and inventing a value would be worse than admitting
    ///     we don't know),
    ///   * admin manual attendance marks, which have no device fix at all,
    ///   * browsers that omit coords.accuracy.
    /// So null means "unknown", never "accurate".
    ///
    /// The pending_checkins column mirrors the coords rule for the QR flow: the
    /// accuracy travels from the CREW's device through the handshake, because the
    /// vendor's scanning phone contributes no position and must contribute no
    /// accuracy either.
    ///
    /// A CHECK constraint rejects negatives and absurd values. 100 km is far
    /// beyond any real fix, so anything above it is a bug or a hostile client
    /// rather than a bad GPS day.
    ///
    /// Idempotent raw SQL, matching this project's migration style.
    ///
    /// >>> Also mirrored in emergencySchemaPatchSql in Program.cs <<<
    /// Migrations do not auto-apply on deploy (RUN_MIGRATIONS_ON_STARTUP gate),
    /// so a migration alone would never reach production. See
    /// docs/DatabaseMigrations.md.
    /// </summary>
    [Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute(typeof(AppDbContext))]
    [Migration("20260822224500_AddAttendanceLocationAccuracy")]
    public partial class AddAttendanceLocationAccuracy : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                ALTER TABLE attendance_records
                    ADD COLUMN IF NOT EXISTS location_accuracy_meters INT NULL;

                ALTER TABLE pending_checkins
                    ADD COLUMN IF NOT EXISTS crew_location_accuracy_meters INT NULL;

                DO $$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM information_schema.constraint_column_usage
                        WHERE  constraint_name = 'ck_attendance_records_accuracy_sane'
                    ) THEN
                        ALTER TABLE attendance_records
                            ADD CONSTRAINT ck_attendance_records_accuracy_sane
                            CHECK (location_accuracy_meters IS NULL
                                   OR location_accuracy_meters BETWEEN 0 AND 100000);
                    END IF;

                    IF NOT EXISTS (
                        SELECT 1 FROM information_schema.constraint_column_usage
                        WHERE  constraint_name = 'ck_pending_checkins_accuracy_sane'
                    ) THEN
                        ALTER TABLE pending_checkins
                            ADD CONSTRAINT ck_pending_checkins_accuracy_sane
                            CHECK (crew_location_accuracy_meters IS NULL
                                   OR crew_location_accuracy_meters BETWEEN 0 AND 100000);
                    END IF;
                END $$;
            ");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                ALTER TABLE pending_checkins
                    DROP CONSTRAINT IF EXISTS ck_pending_checkins_accuracy_sane;
                ALTER TABLE attendance_records
                    DROP CONSTRAINT IF EXISTS ck_attendance_records_accuracy_sane;

                ALTER TABLE pending_checkins
                    DROP COLUMN IF EXISTS crew_location_accuracy_meters;
                ALTER TABLE attendance_records
                    DROP COLUMN IF EXISTS location_accuracy_meters;
            ");
        }
    }
}
