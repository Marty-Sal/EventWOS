using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventWOS.Persistence.Migrations
{
    /// <summary>
    /// Crew Groups module: vendor-scoped named buckets of crew members so
    /// vendors can invite a whole team to an event in one click.
    /// Tables: crew_groups, crew_group_members.
    ///
    /// Uses raw IF NOT EXISTS SQL to match the idempotent pattern used by
    /// every other migration in this project. Safe to re-run.
    /// </summary>
    [Migration("20260605000100_AddCrewGroups")]
    public partial class AddCrewGroups : Migration
    {
        protected override void Up(MigrationBuilder mb)
        {
            mb.Sql(@"
CREATE TABLE IF NOT EXISTS crew_groups (
    id            UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
    vendor_id     UUID NOT NULL,
    name          VARCHAR(120) NOT NULL,
    description   VARCHAR(500),
    created_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
    created_by    UUID,
    updated_at    TIMESTAMPTZ,
    updated_by    UUID,
    is_deleted    BOOLEAN NOT NULL DEFAULT false,
    deleted_at    TIMESTAMPTZ,
    deleted_by    UUID,
    CONSTRAINT fk_crew_groups_vendor
        FOREIGN KEY (vendor_id) REFERENCES users (id) ON DELETE RESTRICT
);

CREATE INDEX IF NOT EXISTS ix_crew_groups_vendor_id
    ON crew_groups (vendor_id);

CREATE INDEX IF NOT EXISTS ix_crew_groups_vendor_name
    ON crew_groups (vendor_id, name);

CREATE TABLE IF NOT EXISTS crew_group_members (
    id             UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
    crew_group_id  UUID NOT NULL,
    crew_id        UUID NOT NULL,
    added_at       TIMESTAMPTZ NOT NULL,
    created_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    created_by     UUID,
    updated_at     TIMESTAMPTZ,
    updated_by     UUID,
    is_deleted     BOOLEAN NOT NULL DEFAULT false,
    deleted_at     TIMESTAMPTZ,
    deleted_by     UUID,
    CONSTRAINT fk_cgm_group
        FOREIGN KEY (crew_group_id) REFERENCES crew_groups (id) ON DELETE CASCADE,
    CONSTRAINT fk_cgm_crew
        FOREIGN KEY (crew_id) REFERENCES users (id) ON DELETE RESTRICT
);

CREATE INDEX IF NOT EXISTS ix_cgm_crew_group_id
    ON crew_group_members (crew_group_id);

CREATE INDEX IF NOT EXISTS ix_cgm_crew_id
    ON crew_group_members (crew_id);

-- Filtered unique index so soft-deleted rows don't block re-adding the same
-- (group, crew) pair after a remove.
CREATE UNIQUE INDEX IF NOT EXISTS ux_cgm_group_crew_active
    ON crew_group_members (crew_group_id, crew_id)
    WHERE is_deleted = false;
");
        }

        protected override void Down(MigrationBuilder mb)
        {
            mb.Sql(@"
DROP TABLE IF EXISTS crew_group_members;
DROP TABLE IF EXISTS crew_groups;
");
        }
    }
}
