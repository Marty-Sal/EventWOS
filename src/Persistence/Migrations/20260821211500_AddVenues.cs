using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventOpsOracle.Persistence.Migrations
{
    /// <summary>
    /// Settings module: the "Venue" catalog. Admin-maintained list of
    /// physical venues with structured address + lat/lng, so an Event can
    /// reuse a saved venue's location instead of re-entering it every time.
    /// Same filtered-unique-name-among-active-rows pattern as scope_of_work.
    ///
    /// Idempotent raw SQL, matching this project's migration style.
    /// </summary>
    [Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute(typeof(AppDbContext))]
    [Migration("20260821211500_AddVenues")]
    public partial class AddVenues : Migration
    {
        protected override void Up(MigrationBuilder mb)
        {
            mb.Sql(@"
CREATE TABLE IF NOT EXISTS venues (
    id                   UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
    name                 VARCHAR(120) NOT NULL,
    address_line1        VARCHAR(200) NOT NULL,
    address_line2        VARCHAR(200),
    city                 VARCHAR(200) NOT NULL,
    state                VARCHAR(100),
    postal_code          VARCHAR(20),
    country              VARCHAR(100),
    latitude             DOUBLE PRECISION,
    longitude            DOUBLE PRECISION,
    notes                VARCHAR(1000),
    created_by_user_id   UUID NOT NULL,
    created_at           TIMESTAMPTZ NOT NULL DEFAULT now(),
    created_by           UUID,
    updated_at           TIMESTAMPTZ,
    updated_by           UUID,
    is_deleted           BOOLEAN NOT NULL DEFAULT false,
    deleted_at           TIMESTAMPTZ,
    deleted_by           UUID
);

CREATE INDEX IF NOT EXISTS ix_venues_name ON venues (name);

CREATE UNIQUE INDEX IF NOT EXISTS ux_venues_name_active
    ON venues (LOWER(name))
    WHERE is_deleted = false;

-- Event.VenueId — optional link so an event can pull its location from a
-- saved venue instead of duplicating lat/lng on every event row.
ALTER TABLE events ADD COLUMN IF NOT EXISTS venue_id UUID NULL;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE constraint_name = 'fk_events_venue_id'
    ) THEN
        ALTER TABLE events
            ADD CONSTRAINT fk_events_venue_id
            FOREIGN KEY (venue_id) REFERENCES venues(id) ON DELETE SET NULL;
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS ix_events_venue_id ON events (venue_id);
");
        }

        protected override void Down(MigrationBuilder mb)
        {
            mb.Sql(@"
                ALTER TABLE events DROP CONSTRAINT IF EXISTS fk_events_venue_id;
                DROP INDEX IF EXISTS ix_events_venue_id;
                ALTER TABLE events DROP COLUMN IF EXISTS venue_id;
                DROP TABLE IF EXISTS venues;
            ");
        }
    }
}
