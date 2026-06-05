using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventWOS.Persistence.Migrations
{
    /// <summary>
    /// Crew Groups module: vendor-scoped named buckets of crew members so
    /// vendors can invite a whole team to an event in one click.
    /// Tables: crew_groups, crew_group_members.
    /// </summary>
    [Migration("20260605000100_AddCrewGroups")]
    public partial class AddCrewGroups : Migration
    {
        protected override void Up(MigrationBuilder mb)
        {
            mb.CreateTable(
                name: "crew_groups",
                columns: t => new
                {
                    id          = t.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    vendor_id   = t.Column<Guid>(type: "uuid", nullable: false),
                    name        = t.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    description = t.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at  = t.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by  = t.Column<Guid>(type: "uuid", nullable: true),
                    updated_at  = t.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by  = t.Column<Guid>(type: "uuid", nullable: true),
                    is_deleted  = t.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    deleted_at  = t.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    deleted_by  = t.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: t =>
                {
                    t.PrimaryKey("pk_crew_groups", x => x.id);
                    t.ForeignKey(
                        name:                  "fk_crew_groups_vendor",
                        column:                x => x.vendor_id,
                        principalTable:        "users",
                        principalColumn:       "id",
                        onDelete:              ReferentialAction.Restrict);
                });

            mb.CreateTable(
                name: "crew_group_members",
                columns: t => new
                {
                    id            = t.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    crew_group_id = t.Column<Guid>(type: "uuid", nullable: false),
                    crew_id       = t.Column<Guid>(type: "uuid", nullable: false),
                    added_at      = t.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_at    = t.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by    = t.Column<Guid>(type: "uuid", nullable: true),
                    updated_at    = t.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by    = t.Column<Guid>(type: "uuid", nullable: true),
                    is_deleted    = t.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    deleted_at    = t.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    deleted_by    = t.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: t =>
                {
                    t.PrimaryKey("pk_crew_group_members", x => x.id);
                    t.ForeignKey(
                        name:                  "fk_cgm_group",
                        column:                x => x.crew_group_id,
                        principalTable:        "crew_groups",
                        principalColumn:       "id",
                        onDelete:              ReferentialAction.Cascade);
                    t.ForeignKey(
                        name:                  "fk_cgm_crew",
                        column:                x => x.crew_id,
                        principalTable:        "users",
                        principalColumn:       "id",
                        onDelete:              ReferentialAction.Restrict);
                });

            mb.CreateIndex("ix_crew_groups_vendor_id",  "crew_groups",       "vendor_id");
            mb.CreateIndex("ix_crew_groups_vendor_name","crew_groups",       new[] { "vendor_id", "name" });
            mb.CreateIndex("ix_cgm_crew_group_id",      "crew_group_members","crew_group_id");
            mb.CreateIndex("ix_cgm_crew_id",            "crew_group_members","crew_id");

            // Filtered unique index so soft-deleted rows don't block re-adding the same pair.
            mb.CreateIndex(
                name:    "ux_cgm_group_crew_active",
                table:   "crew_group_members",
                columns: new[] { "crew_group_id", "crew_id" },
                unique:  true,
                filter:  "is_deleted = false");
        }

        protected override void Down(MigrationBuilder mb)
        {
            mb.DropTable("crew_group_members");
            mb.DropTable("crew_groups");
        }
    }
}
