using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventWOS.Persistence.Migrations
{
    /// <summary>
    /// Corrective migration: aligns DB column names and schema with EF Core entity configurations.
    /// The initial migration SQL used different column names / omitted columns vs what the entities expect.
    /// All operations use idempotent IF EXISTS / IF NOT EXISTS guards — safe to re-run.
    /// </summary>
    [Migration("20260526000000_FixColumnNames")]
    public partial class FixColumnNames : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── users: add columns that may be missing from older DB schema ──────────
            // Original EF-generated schema omitted vendor-specific and crew-specific columns.
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='business_name') THEN
        ALTER TABLE users ADD COLUMN business_name VARCHAR(200);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='referral_code') THEN
        ALTER TABLE users ADD COLUMN referral_code VARCHAR(20);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='rating') THEN
        ALTER TABLE users ADD COLUMN rating NUMERIC(3,2);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='events_completed') THEN
        ALTER TABLE users ADD COLUMN events_completed INT NOT NULL DEFAULT 0;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='vendor_id') THEN
        ALTER TABLE users ADD COLUMN vendor_id UUID;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='discipline_score') THEN
        ALTER TABLE users ADD COLUMN discipline_score NUMERIC(5,2) NOT NULL DEFAULT 100.0;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='events_attended') THEN
        ALTER TABLE users ADD COLUMN events_attended INT NOT NULL DEFAULT 0;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='device_id') THEN
        ALTER TABLE users ADD COLUMN device_id VARCHAR(255);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='last_known_ip') THEN
        ALTER TABLE users ADD COLUMN last_known_ip VARCHAR(45);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='last_login_at') THEN
        ALTER TABLE users ADD COLUMN last_login_at TIMESTAMP;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='failed_otp_attempts') THEN
        ALTER TABLE users ADD COLUMN failed_otp_attempts INT NOT NULL DEFAULT 0;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='locked_until') THEN
        ALTER TABLE users ADD COLUMN locked_until TIMESTAMP;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='manager_id') THEN
        ALTER TABLE users ADD COLUMN manager_id UUID;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='created_by') THEN
        ALTER TABLE users ADD COLUMN created_by UUID;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='updated_at') THEN
        ALTER TABLE users ADD COLUMN updated_at TIMESTAMP;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='updated_by') THEN
        ALTER TABLE users ADD COLUMN updated_by UUID;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='deleted_at') THEN
        ALTER TABLE users ADD COLUMN deleted_at TIMESTAMP;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='deleted_by') THEN
        ALTER TABLE users ADD COLUMN deleted_by UUID;
    END IF;
    -- Referral code unique index (partial, skip if exists)
    IF NOT EXISTS (SELECT 1 FROM pg_indexes WHERE indexname='ix_users_referral_code') THEN
        CREATE UNIQUE INDEX ix_users_referral_code ON users(referral_code) WHERE referral_code IS NOT NULL;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_indexes WHERE indexname='ix_users_vendor_id') THEN
        CREATE INDEX ix_users_vendor_id ON users(vendor_id);
    END IF;
END $$;
");

            // ── otp_requests ─────────────────────────────────────────────────────────
            // Migration created: otp_hash, user_agent, attempts
            // EF config (now fixed): otp_hash ✓, user_agent ✓, attempts ✓
            // Ensure the table exists and has the right columns
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name='otp_requests') THEN
        CREATE TABLE otp_requests (
            id          UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
            mobile      VARCHAR(20) NOT NULL,
            otp_hash    VARCHAR(255) NOT NULL,
            status      INT NOT NULL DEFAULT 0,
            expires_at  TIMESTAMP NOT NULL,
            verified_at TIMESTAMP,
            ip_address  VARCHAR(45),
            user_agent  VARCHAR(500),
            attempts    INT NOT NULL DEFAULT 0,
            created_at  TIMESTAMP NOT NULL DEFAULT now(),
            created_by  UUID,
            updated_at  TIMESTAMP,
            updated_by  UUID,
            is_deleted  BOOL NOT NULL DEFAULT false,
            deleted_at  TIMESTAMP,
            deleted_by  UUID
        );
        CREATE INDEX ix_otp_requests_mobile ON otp_requests(mobile);
        CREATE INDEX ix_otp_requests_expires ON otp_requests(expires_at);
        CREATE INDEX ix_otp_requests_mobile_status ON otp_requests(mobile, status);
    ELSE
        -- Table exists: ensure all columns are present
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='otp_requests' AND column_name='updated_at') THEN
            ALTER TABLE otp_requests ADD COLUMN updated_at TIMESTAMP;
        END IF;
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='otp_requests' AND column_name='updated_by') THEN
            ALTER TABLE otp_requests ADD COLUMN updated_by UUID;
        END IF;
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='otp_requests' AND column_name='deleted_at') THEN
            ALTER TABLE otp_requests ADD COLUMN deleted_at TIMESTAMP;
        END IF;
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='otp_requests' AND column_name='deleted_by') THEN
            ALTER TABLE otp_requests ADD COLUMN deleted_by UUID;
        END IF;
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='otp_requests' AND column_name='created_by') THEN
            ALTER TABLE otp_requests ADD COLUMN created_by UUID;
        END IF;
    END IF;
END $$;
");

            // ── audit_logs ───────────────────────────────────────────────────────────
            // Migration created: actor_id, actor_mobile, entity_name, ip_address, user_agent
            // EF config expects: performed_by_user_id, entity_type, performed_by_ip, additional_data, occurred_at
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name='audit_logs') THEN
        CREATE TABLE audit_logs (
            id                   UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
            performed_by_user_id UUID,
            performed_by_ip      VARCHAR(45),
            action               INT NOT NULL,
            entity_type          VARCHAR(100) NOT NULL,
            entity_id            VARCHAR(50),
            old_values           JSONB,
            new_values           JSONB,
            additional_data      VARCHAR(500),
            occurred_at          TIMESTAMP NOT NULL DEFAULT now(),
            created_at           TIMESTAMP NOT NULL DEFAULT now(),
            created_by           UUID,
            is_deleted           BOOL NOT NULL DEFAULT false
        );
        CREATE INDEX ix_audit_logs_user ON audit_logs(performed_by_user_id);
        CREATE INDEX ix_audit_logs_occurred_at ON audit_logs(occurred_at);
        CREATE INDEX ix_audit_logs_entity ON audit_logs(entity_type, entity_id);
    ELSE
        -- Rename actor_id -> performed_by_user_id
        IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='audit_logs' AND column_name='actor_id') THEN
            ALTER TABLE audit_logs RENAME COLUMN actor_id TO performed_by_user_id;
        END IF;
        -- Drop actor_mobile (not in entity)
        IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='audit_logs' AND column_name='actor_mobile') THEN
            ALTER TABLE audit_logs DROP COLUMN actor_mobile;
        END IF;
        -- Rename entity_name -> entity_type
        IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='audit_logs' AND column_name='entity_name') THEN
            ALTER TABLE audit_logs RENAME COLUMN entity_name TO entity_type;
        END IF;
        -- Rename ip_address -> performed_by_ip
        IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='audit_logs' AND column_name='ip_address') THEN
            ALTER TABLE audit_logs RENAME COLUMN ip_address TO performed_by_ip;
        END IF;
        -- Rename user_agent -> additional_data
        IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='audit_logs' AND column_name='user_agent') THEN
            ALTER TABLE audit_logs RENAME COLUMN user_agent TO additional_data;
        END IF;
        -- Add occurred_at if missing
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='audit_logs' AND column_name='occurred_at') THEN
            ALTER TABLE audit_logs ADD COLUMN occurred_at TIMESTAMP NOT NULL DEFAULT now();
        END IF;
        -- Add additional_data if missing (in case user_agent didn't exist either)
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='audit_logs' AND column_name='additional_data') THEN
            ALTER TABLE audit_logs ADD COLUMN additional_data VARCHAR(500);
        END IF;
        -- Add performed_by_ip if still missing
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='audit_logs' AND column_name='performed_by_ip') THEN
            ALTER TABLE audit_logs ADD COLUMN performed_by_ip VARCHAR(45);
        END IF;
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='audit_logs' AND column_name='entity_type') THEN
            ALTER TABLE audit_logs ADD COLUMN entity_type VARCHAR(100) NOT NULL DEFAULT 'Unknown';
        END IF;
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='audit_logs' AND column_name='performed_by_user_id') THEN
            ALTER TABLE audit_logs ADD COLUMN performed_by_user_id UUID;
        END IF;
    END IF;
    -- Rebuild indexes
    DROP INDEX IF EXISTS ix_audit_user;
    DROP INDEX IF EXISTS ix_audit_entity;
    CREATE INDEX IF NOT EXISTS ix_audit_logs_user ON audit_logs(performed_by_user_id);
    CREATE INDEX IF NOT EXISTS ix_audit_logs_occurred_at ON audit_logs(occurred_at);
    CREATE INDEX IF NOT EXISTS ix_audit_logs_entity ON audit_logs(entity_type, entity_id);
END $$;
");

            // ── user_sessions ────────────────────────────────────────────────────────
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name='user_sessions') THEN
        CREATE TABLE user_sessions (
            id                UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
            user_id           UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
            session_id        UUID NOT NULL DEFAULT gen_random_uuid(),
            device_id         VARCHAR(255) NOT NULL DEFAULT '',
            device_name       VARCHAR(100) NOT NULL DEFAULT '',
            ip_address        VARCHAR(45) NOT NULL DEFAULT '',
            user_agent        VARCHAR(500) NOT NULL DEFAULT '',
            is_active         BOOL NOT NULL DEFAULT true,
            last_activity_at  TIMESTAMP NOT NULL DEFAULT now(),
            terminated_at     TIMESTAMP,
            termination_reason VARCHAR(100),
            created_at        TIMESTAMP NOT NULL DEFAULT now(),
            created_by        UUID,
            updated_at        TIMESTAMP,
            updated_by        UUID,
            is_deleted        BOOL NOT NULL DEFAULT false,
            deleted_at        TIMESTAMP,
            deleted_by        UUID
        );
        CREATE UNIQUE INDEX ix_user_sessions_session_id ON user_sessions(session_id);
        CREATE INDEX ix_user_sessions_user_active ON user_sessions(user_id, is_active);
    ELSE
        -- Add session_id if missing
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='user_sessions' AND column_name='session_id') THEN
            ALTER TABLE user_sessions ADD COLUMN session_id UUID NOT NULL DEFAULT gen_random_uuid();
            CREATE UNIQUE INDEX IF NOT EXISTS ix_user_sessions_session_id ON user_sessions(session_id);
        END IF;
        -- Rename last_seen_at -> last_activity_at
        IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='user_sessions' AND column_name='last_seen_at') THEN
            ALTER TABLE user_sessions RENAME COLUMN last_seen_at TO last_activity_at;
        END IF;
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='user_sessions' AND column_name='last_activity_at') THEN
            ALTER TABLE user_sessions ADD COLUMN last_activity_at TIMESTAMP NOT NULL DEFAULT now();
        END IF;
        -- Rename revoked_at -> terminated_at
        IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='user_sessions' AND column_name='revoked_at') THEN
            ALTER TABLE user_sessions RENAME COLUMN revoked_at TO terminated_at;
        END IF;
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='user_sessions' AND column_name='terminated_at') THEN
            ALTER TABLE user_sessions ADD COLUMN terminated_at TIMESTAMP;
        END IF;
        -- Rename revoked_by -> termination_reason
        IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='user_sessions' AND column_name='revoked_by') THEN
            ALTER TABLE user_sessions RENAME COLUMN revoked_by TO termination_reason;
            ALTER TABLE user_sessions ALTER COLUMN termination_reason TYPE VARCHAR(100) USING termination_reason::TEXT;
        END IF;
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='user_sessions' AND column_name='termination_reason') THEN
            ALTER TABLE user_sessions ADD COLUMN termination_reason VARCHAR(100);
        END IF;
        -- Base audit columns
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='user_sessions' AND column_name='updated_at') THEN
            ALTER TABLE user_sessions ADD COLUMN updated_at TIMESTAMP;
        END IF;
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='user_sessions' AND column_name='updated_by') THEN
            ALTER TABLE user_sessions ADD COLUMN updated_by UUID;
        END IF;
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='user_sessions' AND column_name='deleted_at') THEN
            ALTER TABLE user_sessions ADD COLUMN deleted_at TIMESTAMP;
        END IF;
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='user_sessions' AND column_name='deleted_by') THEN
            ALTER TABLE user_sessions ADD COLUMN deleted_by UUID;
        END IF;
    END IF;
END $$;
");

            // ── refresh_tokens ───────────────────────────────────────────────────────
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name='refresh_tokens') THEN
        CREATE TABLE refresh_tokens (
            id                    UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
            user_id               UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
            token_hash            VARCHAR(255) NOT NULL,
            device_id             VARCHAR(255) NOT NULL DEFAULT '',
            ip_address            VARCHAR(45) NOT NULL DEFAULT '',
            expires_at            TIMESTAMP NOT NULL,
            is_revoked            BOOL NOT NULL DEFAULT false,
            revoked_at            TIMESTAMP,
            replaced_by_token_hash VARCHAR(255),
            revoke_reason         VARCHAR(100),
            created_at            TIMESTAMP NOT NULL DEFAULT now(),
            created_by            UUID,
            updated_at            TIMESTAMP,
            updated_by            UUID,
            is_deleted            BOOL NOT NULL DEFAULT false,
            deleted_at            TIMESTAMP,
            deleted_by            UUID
        );
        CREATE UNIQUE INDEX ix_refresh_tokens_hash ON refresh_tokens(token_hash);
        CREATE INDEX ix_refresh_tokens_user_active ON refresh_tokens(user_id, is_revoked);
    ELSE
        -- Add is_revoked if missing
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='refresh_tokens' AND column_name='is_revoked') THEN
            ALTER TABLE refresh_tokens ADD COLUMN is_revoked BOOL NOT NULL DEFAULT false;
            UPDATE refresh_tokens SET is_revoked = true WHERE revoked_at IS NOT NULL;
        END IF;
        -- Rename replaced_by -> replaced_by_token_hash
        IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='refresh_tokens' AND column_name='replaced_by') THEN
            ALTER TABLE refresh_tokens RENAME COLUMN replaced_by TO replaced_by_token_hash;
        END IF;
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='refresh_tokens' AND column_name='replaced_by_token_hash') THEN
            ALTER TABLE refresh_tokens ADD COLUMN replaced_by_token_hash VARCHAR(255);
        END IF;
        -- Add revoke_reason if missing
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='refresh_tokens' AND column_name='revoke_reason') THEN
            ALTER TABLE refresh_tokens ADD COLUMN revoke_reason VARCHAR(100);
        END IF;
        -- Base audit columns
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='refresh_tokens' AND column_name='updated_at') THEN
            ALTER TABLE refresh_tokens ADD COLUMN updated_at TIMESTAMP;
        END IF;
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='refresh_tokens' AND column_name='updated_by') THEN
            ALTER TABLE refresh_tokens ADD COLUMN updated_by UUID;
        END IF;
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='refresh_tokens' AND column_name='deleted_at') THEN
            ALTER TABLE refresh_tokens ADD COLUMN deleted_at TIMESTAMP;
        END IF;
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='refresh_tokens' AND column_name='deleted_by') THEN
            ALTER TABLE refresh_tokens ADD COLUMN deleted_by UUID;
        END IF;
    END IF;
END $$;
");

            // ── vendor_crew_mappings ─────────────────────────────────────────────────
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name='vendor_crew_mappings') THEN
        CREATE TABLE vendor_crew_mappings (
            id                      UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
            vendor_id               UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
            crew_id                 UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
            approved_by_manager_id  UUID,
            is_active               BOOL NOT NULL DEFAULT true,
            mapped_at               TIMESTAMP NOT NULL DEFAULT now(),
            removed_at              TIMESTAMP,
            notes                   VARCHAR(500),
            created_at              TIMESTAMP NOT NULL DEFAULT now(),
            created_by              UUID,
            updated_at              TIMESTAMP,
            updated_by              UUID,
            is_deleted              BOOL NOT NULL DEFAULT false,
            deleted_at              TIMESTAMP,
            deleted_by              UUID
        );
    ELSE
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='vendor_crew_mappings' AND column_name='updated_at') THEN
            ALTER TABLE vendor_crew_mappings ADD COLUMN updated_at TIMESTAMP;
        END IF;
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='vendor_crew_mappings' AND column_name='updated_by') THEN
            ALTER TABLE vendor_crew_mappings ADD COLUMN updated_by UUID;
        END IF;
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='vendor_crew_mappings' AND column_name='deleted_at') THEN
            ALTER TABLE vendor_crew_mappings ADD COLUMN deleted_at TIMESTAMP;
        END IF;
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='vendor_crew_mappings' AND column_name='deleted_by') THEN
            ALTER TABLE vendor_crew_mappings ADD COLUMN deleted_by UUID;
        END IF;
    END IF;
END $$;
");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reversal intentionally omitted — column additions in prod are one-way.
        }
    }
}
