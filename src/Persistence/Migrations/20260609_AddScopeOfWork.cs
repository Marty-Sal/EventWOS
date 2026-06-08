using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventWOS.Persistence.Migrations
{
    /// <summary>
    /// Phase A of the Scope-of-Work feature: admin-maintained global catalog
    /// of work categories ("Box Office", "Gates", "F&amp;B", etc.).
    ///
    /// Phases B/C/D will add event_shifts, vendor_shift_allocations, and
    /// EventAssignment.shift_id — but those are separate migrations.
    /// This one is intentionally tiny so it can be reverted cleanly if Phase B
    /// reveals a model gap.
    ///
    /// Idempotent raw SQL pattern (memory rule #26). Down() drops the table.
    /// </summary>
    [Migration("20260609000100_AddScopeOfWork")]
    public partial class AddScopeOfWork : Migration
    {
        protected override void Up(MigrationBuilder mb)
        {
            mb.Sql(@"
CREATE TABLE IF NOT EXISTS scope_of_work (
    id                   UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
    name                 VARCHAR(80) NOT NULL,
    description          VARCHAR(500),
    created_by_user_id   UUID NOT NULL,
    created_at           TIMESTAMPTZ NOT NULL DEFAULT now(),
    created_by           UUID,
    updated_at           TIMESTAMPTZ,
    updated_by           UUID,
    is_deleted           BOOLEAN NOT NULL DEFAULT false,
    deleted_at           TIMESTAMPTZ,
    deleted_by           UUID
);

CREATE INDEX IF NOT EXISTS ix_scope_of_work_name
    ON scope_of_work (name);

-- Unique name among ACTIVE rows only. Case-insensitive via LOWER() so
-- ""Box Office"" and ""box office"" collide. Filtered on is_deleted = false
-- so archiving frees the name up for reuse. Matches the filtered-unique
-- pattern from crew_group_members (memory rule #25).
CREATE UNIQUE INDEX IF NOT EXISTS ux_scope_of_work_name_active
    ON scope_of_work (LOWER(name))
    WHERE is_deleted = false;
");
        }

        protected override void Down(MigrationBuilder mb)
        {
            mb.Sql(@"DROP TABLE IF EXISTS scope_of_work;");
        }
    }
}
