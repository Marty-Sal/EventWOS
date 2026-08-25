using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventOpsOracle.Persistence.Migrations
{
    /// <summary>
    /// Adds date_of_birth to users — collected at Crew self-registration to
    /// enforce the 18+ requirement (see User.Age / RegisterCrewValidator).
    /// Nullable: existing/Admin-created accounts never captured it.
    /// Idempotent raw SQL, matching this project's migration style.
    /// </summary>
    [Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute(typeof(AppDbContext))]
    [Migration("20260818221500_AddUserDateOfBirth")]
    public partial class AddUserDateOfBirth : Migration
    {
        protected override void Up(MigrationBuilder mb)
        {
            mb.Sql(@"ALTER TABLE users ADD COLUMN IF NOT EXISTS date_of_birth DATE NULL;");
        }

        protected override void Down(MigrationBuilder mb)
        {
            mb.Sql(@"ALTER TABLE users DROP COLUMN IF EXISTS date_of_birth;");
        }
    }
}
