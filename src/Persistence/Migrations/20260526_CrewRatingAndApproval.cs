using Microsoft.EntityFrameworkCore.Migrations;

namespace EventWOS.Persistence.Migrations;

/// <summary>
/// Adds CrewRating + CrewRatingCount to Users table.
/// Adds VendorRating + RatedAt to EventAssignments table.
/// (Two-step approval columns were added in 20260526_TwoStepApproval.cs)
/// </summary>
public partial class CrewRatingAndApproval : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Users — crew rating fields
        migrationBuilder.Sql(@"
            ALTER TABLE ""Users""
            ADD COLUMN IF NOT EXISTS ""CrewRating""      NUMERIC(4,2)  NULL,
            ADD COLUMN IF NOT EXISTS ""CrewRatingCount"" INTEGER       NOT NULL DEFAULT 0;
        ");

        // EventAssignments — vendor per-assignment rating
        migrationBuilder.Sql(@"
            ALTER TABLE ""EventAssignments""
            ADD COLUMN IF NOT EXISTS ""VendorRating"" NUMERIC(3,1)  NULL,
            ADD COLUMN IF NOT EXISTS ""RatedAt""      TIMESTAMP WITH TIME ZONE NULL;
        ");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(@"
            ALTER TABLE ""Users""
            DROP COLUMN IF EXISTS ""CrewRating"",
            DROP COLUMN IF EXISTS ""CrewRatingCount"";

            ALTER TABLE ""EventAssignments""
            DROP COLUMN IF EXISTS ""VendorRating"",
            DROP COLUMN IF EXISTS ""RatedAt"";
        ");
    }
}
