using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventWOS.Persistence.Migrations
{
    /// <summary>
    /// Location &amp; Geofencing support.
    ///
    /// venues:
    ///   * short_address — compact "locality, city, state" label captured when a
    ///     venue is picked from provider search (display_name is far too long
    ///     for a table row).
    ///   * latitude/longitude widened in *type* from double precision to
    ///     numeric(9,6). Coordinates are fixed-precision decimal data; 6 dp is
    ///     ~11 cm, finer than any consumer GPS. Pinning the scale in the
    ///     database stops the same point from being stored with differing
    ///     trailing digits depending on the write path. The USING cast is safe:
    ///     it only rounds, and precision 9 covers -180..180.
    ///
    /// events:
    ///   * geo_fence_enabled / geo_fence_radius_meters — the attendance
    ///     boundary. Deliberately on events rather than venues: two events at
    ///     the same venue routinely need different radii (100 m for one hall,
    ///     300 m for a stadium-wide festival). The radius is NOT NULL-able only
    ///     when the fence is armed, enforced by a CHECK constraint so the
    ///     database itself refuses an armed fence with no radius.
    ///
    /// Idempotent raw SQL, matching this project's migration style.
    ///
    /// >>> Also mirrored in emergencySchemaPatchSql in Program.cs <<<
    /// Migrations do not auto-apply on deploy (RUN_MIGRATIONS_ON_STARTUP gate),
    /// so a migration alone would never reach production. See
    /// docs/DatabaseMigrations.md.
    /// </summary>
    [Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute(typeof(AppDbContext))]
    [Migration("20260822213000_AddVenueGeofencing")]
    public partial class AddVenueGeofencing : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                -- ═══ venues ═══════════════════════════════════════════════
                ALTER TABLE venues
                    ADD COLUMN IF NOT EXISTS short_address VARCHAR(200) NULL;

                ALTER TABLE venues
                    ALTER COLUMN latitude  TYPE NUMERIC(9,6) USING ROUND(latitude::numeric,  6),
                    ALTER COLUMN longitude TYPE NUMERIC(9,6) USING ROUND(longitude::numeric, 6);

                -- ═══ events ═══════════════════════════════════════════════
                ALTER TABLE events
                    ADD COLUMN IF NOT EXISTS geo_fence_enabled       BOOLEAN NOT NULL DEFAULT FALSE,
                    ADD COLUMN IF NOT EXISTS geo_fence_radius_meters INT     NULL;

                -- Belt-and-braces invariants. The domain (Event.EnableGeoFence)
                -- is the primary guard; these stop a stray SQL update or a
                -- future code path from persisting a fence that the attendance
                -- checker could not evaluate.
                DO $$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM information_schema.constraint_column_usage
                        WHERE  constraint_name = 'ck_events_geo_fence_radius'
                    ) THEN
                        ALTER TABLE events ADD CONSTRAINT ck_events_geo_fence_radius
                            CHECK (
                                -- Fence off: radius must be absent.
                                (geo_fence_enabled = FALSE AND geo_fence_radius_meters IS NULL)
                                -- Fence on: radius present, sane, and anchored
                                -- to a venue we can measure from.
                                OR (geo_fence_enabled = TRUE
                                    AND geo_fence_radius_meters IS NOT NULL
                                    AND geo_fence_radius_meters BETWEEN 20 AND 5000
                                    AND venue_id IS NOT NULL)
                            );
                    END IF;
                END $$;
            ");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                ALTER TABLE events DROP CONSTRAINT IF EXISTS ck_events_geo_fence_radius;
                ALTER TABLE events DROP COLUMN IF EXISTS geo_fence_radius_meters;
                ALTER TABLE events DROP COLUMN IF EXISTS geo_fence_enabled;

                ALTER TABLE venues
                    ALTER COLUMN latitude  TYPE DOUBLE PRECISION USING latitude::double precision,
                    ALTER COLUMN longitude TYPE DOUBLE PRECISION USING longitude::double precision;
                ALTER TABLE venues DROP COLUMN IF EXISTS short_address;
            ");
        }
    }
}
