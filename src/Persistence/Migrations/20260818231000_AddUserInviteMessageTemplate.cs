using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventOpsOracle.Persistence.Migrations
{
    /// <summary>
    /// Adds invite_message_template to users — lets a Vendor customize the
    /// share text used alongside their crew invite link (Profile.razor).
    /// Nullable/optional: falls back to the app's default share copy when unset.
    /// Idempotent raw SQL, matching this project's migration style.
    /// </summary>
    [Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute(typeof(AppDbContext))]
    [Migration("20260818231000_AddUserInviteMessageTemplate")]
    public partial class AddUserInviteMessageTemplate : Migration
    {
        protected override void Up(MigrationBuilder mb)
        {
            mb.Sql(@"ALTER TABLE users ADD COLUMN IF NOT EXISTS invite_message_template VARCHAR(500) NULL;");
        }

        protected override void Down(MigrationBuilder mb)
        {
            mb.Sql(@"ALTER TABLE users DROP COLUMN IF EXISTS invite_message_template;");
        }
    }
}
