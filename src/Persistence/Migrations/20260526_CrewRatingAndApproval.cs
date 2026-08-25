using Microsoft.EntityFrameworkCore.Migrations;

namespace EventOpsOracle.Persistence.Migrations;

/// <summary>
/// Adds CrewRating + CrewRatingCount to Users table.
/// Adds VendorRating + RatedAt to EventAssignments table.
/// (Two-step approval columns were added in 20260526_TwoStepApproval.cs)
/// </summary>
[Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute(typeof(AppDbContext))]
[Migration("20260529000300_CrewRatingAndApproval")]
public partial class CrewRatingAndApproval : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Users — crew rating fields
        migrationBuilder.Sql(@"
            ALTER TABLE users
            ADD COLUMN IF NOT EXISTS crew_rating      NUMERIC(4,2)  NULL,
            ADD COLUMN IF NOT EXISTS crew_rating_count INTEGER       NOT NULL DEFAULT 0;
        ");

        // EventAssignments — vendor per-assignment rating
        migrationBuilder.Sql(@"
            ALTER TABLE event_assignments
            ADD COLUMN IF NOT EXISTS vendor_rating NUMERIC(3,1)  NULL,
            ADD COLUMN IF NOT EXISTS rated_at      TIMESTAMP WITH TIME ZONE NULL;
        ");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(@"
            ALTER TABLE users
            DROP COLUMN IF EXISTS crew_rating,
            DROP COLUMN IF EXISTS crew_rating_count;

            ALTER TABLE event_assignments
            DROP COLUMN IF EXISTS vendor_rating,
            DROP COLUMN IF EXISTS rated_at;
        ");
    }
}
