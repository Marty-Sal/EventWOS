using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventWOS.Persistence.Migrations
{
    /// <summary>
    /// Phase D step 19 — relax the (event_id, crew_id) uniqueness constraint
    /// on <c>event_assignments</c> so the SAME crew member can be assigned
    /// to MULTIPLE shifts within ONE event.
    ///
    /// Why: a single event might have Box Office (10:15 – 13:00) and F&B
    /// (14:00 – 18:00) and a multi-skilled crew member should be invitable
    /// to both. The old unique <c>ix_event_assignments_event_crew_unique
    /// (event_id, crew_id) WHERE is_deleted = false</c> rejected the second
    /// invite with a DB-level duplicate-key error, surfaced to the vendor
    /// as "That crew is already on this event." even though they were on
    /// a different shift.
    ///
    /// New uniqueness: <c>(event_id, crew_id, shift_id)</c> with the same
    /// soft-delete filter. Placeholders (crew_id IS NULL) keep their
    /// natural behaviour — Postgres treats NULLs as distinct in unique
    /// indexes, so multiple placeholder rows per (event, shift) still
    /// coexist (we use other mechanisms to control placeholder counts).
    ///
    /// Strictly more permissive than the previous index → safe to apply
    /// to any existing data (no rows could violate the new key without
    /// also violating the old one).
    ///
    /// Idempotent: drops the old index by name; creates the new one with
    /// IF NOT EXISTS. Safe to re-run.
    /// </summary>
    public partial class RelaxEventCrewUniqueToShift : Migration
    {
        protected override void Up(MigrationBuilder mb)
        {
            mb.Sql(@"
                -- Drop the old (event_id, crew_id) unique. Both index
                -- names are tried because earlier dev DBs may have used
                -- the EF-generated default name.
                DROP INDEX IF EXISTS ix_event_assignments_event_crew_unique;
                DROP INDEX IF EXISTS ""IX_event_assignments_event_id_crew_id"";

                -- New per-shift unique. Placeholders (crew_id NULL) are
                -- excluded by Postgres' NULL-distinct semantics — they
                -- never collide with each other on this index.
                CREATE UNIQUE INDEX IF NOT EXISTS ix_event_assignments_event_crew_shift_unique
                    ON event_assignments (event_id, crew_id, shift_id)
                    WHERE is_deleted = false;
            ");
        }

        protected override void Down(MigrationBuilder mb)
        {
            mb.Sql(@"
                DROP INDEX IF EXISTS ix_event_assignments_event_crew_shift_unique;

                -- WARNING: re-creating the legacy unique can fail if any
                -- (event, crew) pair now spans multiple shifts. Down() is
                -- best-effort; the failure mode is intentional so an
                -- operator notices before rolling back.
                CREATE UNIQUE INDEX IF NOT EXISTS ix_event_assignments_event_crew_unique
                    ON event_assignments (event_id, crew_id) WHERE is_deleted = false;
            ");
        }
    }
}
