using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventOpsOracle.Persistence.Migrations
{
    /// <summary>
    /// Adds invited_by_user_id + profile_completed_at to users — tracks
    /// accounts directly added by an Admin/Vendor (CreateVendorCommand /
    /// CreateCrewCommand), which skip the approval queue and self-registration
    /// entirely. Lets us notify the inviter once the invitee fills in their
    /// profile for the first time.
    ///
    /// Also upgrades ix_users_email from a plain index to a unique, filtered
    /// (email IS NOT NULL) index — closes the gap where CreateVendorCommand /
    /// CreateCrewCommand / CreateManagerCommand never checked email uniqueness
    /// (only mobile). Existing emails are normalized (trim + lowercase) first
    /// so the new unique index doesn't fail on legacy casing differences.
    /// Idempotent raw SQL, matching this project's migration style.
    /// </summary>
    [Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute(typeof(AppDbContext))]
    [Migration("20260821030000_AddInvitedByAndProfileCompletion")]
    public partial class AddInvitedByAndProfileCompletion : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                ALTER TABLE users
                    ADD COLUMN IF NOT EXISTS invited_by_user_id  UUID NULL,
                    ADD COLUMN IF NOT EXISTS profile_completed_at TIMESTAMPTZ NULL;

                DO $$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM information_schema.table_constraints
                        WHERE constraint_name = 'fk_users_invited_by_user_id'
                    ) THEN
                        ALTER TABLE users
                            ADD CONSTRAINT fk_users_invited_by_user_id
                            FOREIGN KEY (invited_by_user_id) REFERENCES users(id) ON DELETE SET NULL;
                    END IF;
                END $$;

                -- Normalize existing emails before enforcing uniqueness — self-registration
                -- already stored lowercase, but pre-existing Admin/Vendor-added rows didn't.
                UPDATE users SET email = LOWER(TRIM(email)) WHERE email IS NOT NULL;

                DROP INDEX IF EXISTS ix_users_email;
                CREATE UNIQUE INDEX IF NOT EXISTS ix_users_email
                    ON users (email) WHERE email IS NOT NULL;
            ");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DROP INDEX IF EXISTS ix_users_email;
                CREATE INDEX IF NOT EXISTS ix_users_email ON users (email);

                ALTER TABLE users DROP CONSTRAINT IF EXISTS fk_users_invited_by_user_id;
                ALTER TABLE users
                    DROP COLUMN IF EXISTS invited_by_user_id,
                    DROP COLUMN IF EXISTS profile_completed_at;
            ");
        }
    }
}
