using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventWOS.Persistence.Migrations
{
    /// <summary>
    /// Phase 1 of self-registration: add password-based auth columns and
    /// extended profile columns to users. Adds the 'Rejected' UserStatus
    /// implicitly (it's just int = 4, no schema change for the column itself).
    ///
    /// Backfills existing rows:
    ///   - username = mobile (lowercased)
    ///   - require_password_reset = true  (grandfathered users complete
    ///     OTP-driven setup on next login)
    /// New self-registered users obviously start with username + password
    /// already set by the registration handler.
    ///
    /// Idempotent IF NOT EXISTS pattern — safe to re-run.
    /// </summary>
    [Migration("20260608000100_AddSelfRegistration")]
    public partial class AddSelfRegistration : Migration
    {
        protected override void Up(MigrationBuilder mb)
        {
            mb.Sql(@"
ALTER TABLE users
    ADD COLUMN IF NOT EXISTS username                  VARCHAR(50),
    ADD COLUMN IF NOT EXISTS password_hash             VARCHAR(255),
    ADD COLUMN IF NOT EXISTS require_password_reset    BOOLEAN NOT NULL DEFAULT FALSE,
    ADD COLUMN IF NOT EXISTS failed_login_attempts     INT     NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS last_password_change_at   TIMESTAMPTZ,
    ADD COLUMN IF NOT EXISTS rejected_at               TIMESTAMPTZ,
    ADD COLUMN IF NOT EXISTS rejection_reason          VARCHAR(500),
    ADD COLUMN IF NOT EXISTS rejected_by_user_id       UUID,
    ADD COLUMN IF NOT EXISTS approved_at               TIMESTAMPTZ,
    ADD COLUMN IF NOT EXISTS approved_by_user_id       UUID,
    ADD COLUMN IF NOT EXISTS contact_person_name       VARCHAR(150),
    ADD COLUMN IF NOT EXISTS gst_number                VARCHAR(50),
    ADD COLUMN IF NOT EXISTS address                   VARCHAR(500),
    ADD COLUMN IF NOT EXISTS city                      VARCHAR(100),
    ADD COLUMN IF NOT EXISTS state                     VARCHAR(100),
    ADD COLUMN IF NOT EXISTS website                   VARCHAR(255),
    ADD COLUMN IF NOT EXISTS bio                       VARCHAR(2000),
    ADD COLUMN IF NOT EXISTS skills                    VARCHAR(500),
    ADD COLUMN IF NOT EXISTS experience_years          INT,
    ADD COLUMN IF NOT EXISTS referral_code_used        VARCHAR(20);

-- Backfill: username = lowercase mobile, force password setup on next login.
UPDATE users
   SET username = LOWER(mobile),
       require_password_reset = TRUE
 WHERE username IS NULL;

-- Unique index on username (filtered, so soft-deleted/null rows don't clash).
CREATE UNIQUE INDEX IF NOT EXISTS ix_users_username
    ON users (username)
    WHERE username IS NOT NULL;

-- Lookup index for the 24h re-registration cool-down query.
CREATE INDEX IF NOT EXISTS ix_users_rejected_at
    ON users (rejected_at)
    WHERE rejected_at IS NOT NULL;
");
        }

        protected override void Down(MigrationBuilder mb)
        {
            mb.Sql(@"
DROP INDEX IF EXISTS ix_users_username;
DROP INDEX IF EXISTS ix_users_rejected_at;

ALTER TABLE users
    DROP COLUMN IF EXISTS username,
    DROP COLUMN IF EXISTS password_hash,
    DROP COLUMN IF EXISTS require_password_reset,
    DROP COLUMN IF EXISTS failed_login_attempts,
    DROP COLUMN IF EXISTS last_password_change_at,
    DROP COLUMN IF EXISTS rejected_at,
    DROP COLUMN IF EXISTS rejection_reason,
    DROP COLUMN IF EXISTS rejected_by_user_id,
    DROP COLUMN IF EXISTS approved_at,
    DROP COLUMN IF EXISTS approved_by_user_id,
    DROP COLUMN IF EXISTS contact_person_name,
    DROP COLUMN IF EXISTS gst_number,
    DROP COLUMN IF EXISTS address,
    DROP COLUMN IF EXISTS city,
    DROP COLUMN IF EXISTS state,
    DROP COLUMN IF EXISTS website,
    DROP COLUMN IF EXISTS bio,
    DROP COLUMN IF EXISTS skills,
    DROP COLUMN IF EXISTS experience_years,
    DROP COLUMN IF EXISTS referral_code_used;
");
        }
    }
}
