using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventWOS.Persistence.Migrations
{
    /// <summary>
    /// Corrective migration: aligns DB column names with EF Core entity configurations.
    /// The initial migration used different column names than the entity configs expected.
    /// This is safe to re-run — all operations use IF EXISTS / DO $$ guards.
    /// </summary>
    [Migration("20260526000000_FixColumnNames")]
    public partial class FixColumnNames : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── otp_requests ─────────────────────────────────────────────────────────
            // Migration created: otp_hash, user_agent (no device_id), attempts
            // EF config expects: otp_hash ✓, user_agent ✓ (mapped to DeviceId), attempts ✓
            // No renames needed — OtpRequestConfiguration already updated to match.

            // ── audit_logs ───────────────────────────────────────────────────────────
            // Migration created: actor_id, actor_mobile, action, entity_name, entity_id,
            //                    old_values, new_values, ip_address, user_agent
            // EF config expects: performed_by_user_id, performed_by_ip, action, entity_type,
            //                    entity_id, old_values, new_values, additional_data, occurred_at
            migrationBuilder.Sql(@"
DO $$
BEGIN
    -- Rename actor_id -> performed_by_user_id
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='audit_logs' AND column_name='actor_id') THEN
        ALTER TABLE audit_logs RENAME COLUMN actor_id TO performed_by_user_id;
    END IF;
    -- Rename actor_mobile -> (drop it — not in entity)
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
    -- Rename user_agent -> additional_data (repurpose for AdditionalData)
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='audit_logs' AND column_name='user_agent') THEN
        ALTER TABLE audit_logs RENAME COLUMN user_agent TO additional_data;
    END IF;
    -- Add occurred_at if missing
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='audit_logs' AND column_name='occurred_at') THEN
        ALTER TABLE audit_logs ADD COLUMN occurred_at TIMESTAMP NOT NULL DEFAULT now();
    END IF;
    -- Set occurred_at from created_at for existing rows
    UPDATE audit_logs SET occurred_at = created_at WHERE occurred_at = '0001-01-01';
END $$;
");

            // ── user_sessions ────────────────────────────────────────────────────────
            // Migration created: user_id, device_id, device_name, ip_address, user_agent,
            //                    is_active, last_seen_at, revoked_at, revoked_by
            // EF config expects: user_id ✓, session_id (NEW), device_id ✓, device_name ✓,
            //                    ip_address ✓, user_agent ✓, is_active ✓,
            //                    last_activity_at, terminated_at, termination_reason
            migrationBuilder.Sql(@"
DO $$
BEGIN
    -- Add session_id if missing
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='user_sessions' AND column_name='session_id') THEN
        ALTER TABLE user_sessions ADD COLUMN session_id UUID NOT NULL DEFAULT gen_random_uuid();
        CREATE UNIQUE INDEX IF NOT EXISTS ix_user_sessions_session_id ON user_sessions(session_id);
    END IF;
    -- Rename last_seen_at -> last_activity_at
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='user_sessions' AND column_name='last_seen_at') THEN
        ALTER TABLE user_sessions RENAME COLUMN last_seen_at TO last_activity_at;
    END IF;
    -- Rename revoked_at -> terminated_at
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='user_sessions' AND column_name='revoked_at') THEN
        ALTER TABLE user_sessions RENAME COLUMN revoked_at TO terminated_at;
    END IF;
    -- Rename revoked_by -> termination_reason (VARCHAR repurpose)
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='user_sessions' AND column_name='revoked_by') THEN
        ALTER TABLE user_sessions RENAME COLUMN revoked_by TO termination_reason;
        -- Change type from UUID to VARCHAR if needed
        ALTER TABLE user_sessions ALTER COLUMN termination_reason TYPE VARCHAR(100) USING termination_reason::TEXT;
    END IF;
    -- Add missing base entity audit columns if absent
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
END $$;
");

            // ── refresh_tokens ───────────────────────────────────────────────────────
            // Migration created: user_id, token_hash, device_id, expires_at,
            //                    revoked_at, replaced_by, ip_address
            // EF config expects: user_id ✓, token_hash ✓, device_id ✓, expires_at ✓,
            //                    is_revoked (NEW bool), revoked_at ✓,
            //                    replaced_by_token_hash, revoke_reason, ip_address ✓
            migrationBuilder.Sql(@"
DO $$
BEGIN
    -- Add is_revoked if missing
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='refresh_tokens' AND column_name='is_revoked') THEN
        ALTER TABLE refresh_tokens ADD COLUMN is_revoked BOOL NOT NULL DEFAULT false;
        -- Back-fill: if revoked_at is set, mark is_revoked = true
        UPDATE refresh_tokens SET is_revoked = true WHERE revoked_at IS NOT NULL;
    END IF;
    -- Rename replaced_by -> replaced_by_token_hash
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='refresh_tokens' AND column_name='replaced_by') THEN
        ALTER TABLE refresh_tokens RENAME COLUMN replaced_by TO replaced_by_token_hash;
    END IF;
    -- Add revoke_reason if missing
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='refresh_tokens' AND column_name='revoke_reason') THEN
        ALTER TABLE refresh_tokens ADD COLUMN revoke_reason VARCHAR(100);
    END IF;
    -- Add missing base entity audit columns
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
END $$;
");

            // ── vendor_crew_mappings ─────────────────────────────────────────────────
            // Verify deleted_at / deleted_by present (already in migration)
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='vendor_crew_mappings' AND column_name='updated_at') THEN
        ALTER TABLE vendor_crew_mappings ADD COLUMN updated_at TIMESTAMP;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='vendor_crew_mappings' AND column_name='updated_by') THEN
        ALTER TABLE vendor_crew_mappings ADD COLUMN updated_by UUID;
    END IF;
END $$;
");

            // ── Rebuild indexes for audit_logs with new column names ──────────────────
            migrationBuilder.Sql(@"
DROP INDEX IF EXISTS ix_audit_user;
DROP INDEX IF EXISTS ix_audit_entity;
CREATE INDEX IF NOT EXISTS ix_audit_logs_user ON audit_logs(performed_by_user_id);
CREATE INDEX IF NOT EXISTS ix_audit_logs_occurred_at ON audit_logs(occurred_at);
CREATE INDEX IF NOT EXISTS ix_audit_logs_entity ON audit_logs(entity_type, entity_id);
");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reversal is intentionally omitted — column renames in prod are one-way.
            // Restore from backup if rollback is needed.
        }
    }
}
