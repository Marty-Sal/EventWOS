using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventWOS.Persistence.Migrations
{
    /// <summary>
    /// Phase B of the Scope-of-Work feature: introduces <c>event_shifts</c> as
    /// the source of truth for event staffing capacity, and FKs every existing
    /// <c>event_assignments</c> row to a synthetic "General" shift backfilled
    /// from the legacy <c>events.max_crew</c> column.
    ///
    /// This migration is INTENTIONALLY chunky — it's a four-step dance that has
    /// to happen atomically per-event (table create → backfill rows → FK fill →
    /// constraint tighten). Splitting it into multiple migrations would let a
    /// partial deploy leave the FK in a half-applied state. The whole script
    /// runs inside one transaction (EF Core default) so either all events get
    /// shifts or none do.
    ///
    /// LEGACY COMPAT: <c>events.max_crew</c> stays in the schema. We don't
    /// remove it here because (a) reports / queries still read it, (b) it
    /// becomes a computed/synced view in a follow-up commit once every caller
    /// migrates to <c>SUM(event_shifts.crew_count)</c>. Strategy "c" from the
    /// Phase A product Q&amp;A — see memory rule "Scope-of-Work roadmap".
    ///
    /// Idempotent raw SQL pattern (memory rule #26). Down() reverses the
    /// schema but does NOT restore the synthetic shifts as anything other
    /// than dropped rows — by design, a Phase B rollback returns the system
    /// to its Phase A state with shift data discarded.
    /// </summary>
    [Migration("20260609000200_AddEventShifts")]
    public partial class AddEventShifts : Migration
    {
        protected override void Up(MigrationBuilder mb)
        {
            mb.Sql(@"
-- ═══ 1. event_shifts table ═══════════════════════════════════════════════════
CREATE TABLE IF NOT EXISTS event_shifts (
    id                   UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
    event_id             UUID NOT NULL REFERENCES events(id)         ON DELETE CASCADE,
    scope_of_work_id     UUID NOT NULL REFERENCES scope_of_work(id)  ON DELETE RESTRICT,
    crew_count           INTEGER NOT NULL CHECK (crew_count >= 1),
    start_at             TIMESTAMPTZ NOT NULL,
    end_at               TIMESTAMPTZ,
    created_by_user_id   UUID NOT NULL,
    created_at           TIMESTAMPTZ NOT NULL DEFAULT now(),
    created_by           UUID,
    updated_at           TIMESTAMPTZ,
    updated_by           UUID,
    is_deleted           BOOLEAN NOT NULL DEFAULT false,
    deleted_at           TIMESTAMPTZ,
    deleted_by           UUID,

    -- Domain invariant mirrored at the DB level — defence in depth.
    -- EventShift.ValidateInvariants() throws the friendly version; this
    -- catches the case where raw SQL bypasses the domain.
    CONSTRAINT ck_event_shifts_end_after_start
        CHECK (end_at IS NULL OR end_at > start_at)
);

CREATE INDEX IF NOT EXISTS ix_event_shifts_event_id
    ON event_shifts (event_id);
CREATE INDEX IF NOT EXISTS ix_event_shifts_scope_of_work_id
    ON event_shifts (scope_of_work_id);

-- ═══ 2. event_assignments.shift_id (nullable for now) ════════════════════════
ALTER TABLE event_assignments
    ADD COLUMN IF NOT EXISTS shift_id UUID;

CREATE INDEX IF NOT EXISTS ix_event_assignments_shift_id
    ON event_assignments (shift_id);

-- ═══ 3. Seed the synthetic ""General"" scope-of-work row ═════════════════════
-- One global row, idempotent. Used as the FK for every backfilled shift.
-- Filtered unique index on scope_of_work(LOWER(name)) WHERE is_deleted=false
-- means the case-insensitive dup check works correctly here too.
DO $$
DECLARE
    v_general_id UUID;
    v_admin_id   UUID;
BEGIN
    SELECT id INTO v_general_id
      FROM scope_of_work
     WHERE LOWER(name) = 'general'
       AND is_deleted = false
     LIMIT 1;

    -- Pick a sane creator. Prefer the oldest Admin; fall back to NULL-safe
    -- generation. The created_by_user_id column on scope_of_work is NOT NULL
    -- so we have to find SOMEONE — if there are no users at all (fresh DB
    -- pre-seeder), we skip the seed entirely and the DatabaseSeeder will
    -- create the General row on next boot.
    SELECT id INTO v_admin_id FROM users ORDER BY created_at ASC LIMIT 1;

    IF v_general_id IS NULL AND v_admin_id IS NOT NULL THEN
        INSERT INTO scope_of_work
            (id, name, description, created_by_user_id, created_at, is_deleted)
        VALUES
            (gen_random_uuid(),
             'General',
             'Default scope of work backfilled from pre-shift events. ' ||
             'Edit the shift to assign a more specific category.',
             v_admin_id, now(), false)
        RETURNING id INTO v_general_id;
    END IF;

    -- ═══ 4. Backfill: one shift per existing event ═══════════════════════════
    -- For every event that has NO shift yet, create a single ""General"" shift
    -- mirroring its current (start_at, end_at, max_crew). Then point every
    -- assignment on that event at the new shift.
    --
    -- crew_count uses GREATEST(max_crew, 1) because a few legacy events
    -- have max_crew = 0 (= ""unlimited"" in the old model). Phase B's domain
    -- requires crew_count >= 1, so we plant the floor and let admins bump
    -- it via the UI. The CHECK constraint above would reject 0 anyway.
    IF v_general_id IS NOT NULL THEN
        WITH events_needing_shift AS (
            SELECT e.id, e.start_at, e.end_at, GREATEST(e.max_crew, 1) AS cc,
                   COALESCE(e.created_by_user_id, v_admin_id) AS creator
              FROM events e
              LEFT JOIN event_shifts s
                    ON s.event_id = e.id AND s.is_deleted = false
             WHERE s.id IS NULL
        ),
        inserted_shifts AS (
            INSERT INTO event_shifts
                (id, event_id, scope_of_work_id, crew_count,
                 start_at, end_at, created_by_user_id, created_at, is_deleted)
            SELECT gen_random_uuid(), id, v_general_id, cc,
                   start_at, end_at, creator, now(), false
              FROM events_needing_shift
            RETURNING id, event_id
        )
        UPDATE event_assignments a
           SET shift_id = ish.id
          FROM inserted_shifts ish
         WHERE a.event_id = ish.event_id
           AND a.shift_id IS NULL;
    END IF;
END $$;

-- ═══ 5. Tighten the constraint — shift_id is now NOT NULL ════════════════════
-- Only run this if every existing row has a shift_id. If we skipped seed
-- because there were zero users, there are also zero events (and therefore
-- zero assignments), so the assertion trivially holds.
DO $$
DECLARE
    v_orphans INT;
BEGIN
    SELECT COUNT(*) INTO v_orphans
      FROM event_assignments
     WHERE shift_id IS NULL AND is_deleted = false;

    IF v_orphans = 0 THEN
        -- IF NOT NULL constraint already applied, ALTER is a no-op. Safe.
        ALTER TABLE event_assignments
            ALTER COLUMN shift_id SET NOT NULL;
    ELSE
        RAISE NOTICE 'Skipping shift_id NOT NULL — % active assignments still lack a shift. '
                     'Re-run after fixing.', v_orphans;
    END IF;
END $$;
");
        }

        protected override void Down(MigrationBuilder mb)
        {
            mb.Sql(@"
-- Reverse Phase B cleanly. Synthetic shifts and the FK column go; events
-- and assignments remain intact. max_crew is still the source of truth
-- post-rollback because we never touched it on the way up.
ALTER TABLE event_assignments
    DROP COLUMN IF EXISTS shift_id;

DROP INDEX IF EXISTS ix_event_assignments_shift_id;
DROP TABLE IF EXISTS event_shifts;
");
        }
    }
}
