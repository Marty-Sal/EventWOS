using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventOpsOracle.Persistence.Migrations
{
    /// <summary>
    /// Reference-data table: the 28 Indian states + 8 union territories.
    /// Every "State" field in the app (Venue catalog, Vendor/Crew
    /// registration, profile editing) is meant to read from this table via
    /// a dropdown instead of free text. Rows are seeded by DatabaseSeeder,
    /// not by this migration — this only creates the table.
    ///
    /// Idempotent raw SQL, matching this project's migration style.
    /// </summary>
    [Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute(typeof(AppDbContext))]
    [Migration("20260821224500_AddIndianStatesTable")]
    public partial class AddIndianStatesTable : Migration
    {
        protected override void Up(MigrationBuilder mb)
        {
            mb.Sql(@"
CREATE TABLE IF NOT EXISTS indian_states (
    id                   UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
    name                 VARCHAR(100) NOT NULL,
    is_union_territory   BOOLEAN NOT NULL DEFAULT false,
    sort_order           INTEGER NOT NULL DEFAULT 0,
    created_at           TIMESTAMPTZ NOT NULL DEFAULT now(),
    created_by           UUID,
    updated_at           TIMESTAMPTZ,
    updated_by           UUID,
    is_deleted           BOOLEAN NOT NULL DEFAULT false,
    deleted_at           TIMESTAMPTZ,
    deleted_by           UUID
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_indian_states_name ON indian_states (name);
");
        }

        protected override void Down(MigrationBuilder mb)
        {
            mb.Sql(@"DROP TABLE IF EXISTS indian_states;");
        }
    }
}
