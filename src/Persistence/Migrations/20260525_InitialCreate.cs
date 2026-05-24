using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventWOS.Persistence.Migrations
{
    [Migration("20260525000000_InitialCreate")]
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── Create tables only if they don't exist (idempotent) ────────────
            migrationBuilder.Sql(@"
CREATE TABLE IF NOT EXISTS users (
    id                  UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
    mobile              VARCHAR(20) NOT NULL,
    full_name           VARCHAR(100) NOT NULL,
    email               VARCHAR(255),
    avatar_url          VARCHAR(500),
    role                INT NOT NULL,
    status              INT NOT NULL DEFAULT 0,
    manager_id          UUID,
    device_id           VARCHAR(255),
    last_known_ip       VARCHAR(45),
    last_login_at       TIMESTAMP,
    failed_otp_attempts INT NOT NULL DEFAULT 0,
    locked_until        TIMESTAMP,
    business_name       VARCHAR(200),
    referral_code       VARCHAR(20),
    rating              DECIMAL(3,2),
    events_completed    INT NOT NULL DEFAULT 0,
    vendor_id           UUID,
    discipline_score    DECIMAL(5,2) NOT NULL DEFAULT 100.00,
    events_attended     INT NOT NULL DEFAULT 0,
    created_at          TIMESTAMP NOT NULL DEFAULT now(),
    created_by          UUID,
    updated_at          TIMESTAMP,
    updated_by          UUID,
    is_deleted          BOOL NOT NULL DEFAULT false,
    deleted_at          TIMESTAMP,
    deleted_by          UUID
);");

            migrationBuilder.Sql(@"
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'fk_users_manager') THEN
        ALTER TABLE users ADD CONSTRAINT fk_users_manager FOREIGN KEY (manager_id) REFERENCES users(id) ON DELETE SET NULL;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'fk_users_vendor') THEN
        ALTER TABLE users ADD CONSTRAINT fk_users_vendor FOREIGN KEY (vendor_id) REFERENCES users(id) ON DELETE SET NULL;
    END IF;
END $$;");

            migrationBuilder.Sql(@"
CREATE UNIQUE INDEX IF NOT EXISTS ix_users_mobile ON users(mobile);
CREATE INDEX IF NOT EXISTS ix_users_email ON users(email);
CREATE INDEX IF NOT EXISTS ix_users_role ON users(role);
CREATE INDEX IF NOT EXISTS ix_users_status ON users(status);
CREATE INDEX IF NOT EXISTS ix_users_vendor_id ON users(vendor_id);
CREATE INDEX IF NOT EXISTS ix_users_soft_delete_status ON users(is_deleted, status);
CREATE UNIQUE INDEX IF NOT EXISTS ix_users_referral_code ON users(referral_code) WHERE referral_code IS NOT NULL;");

            // Add new columns to existing users table if they don't exist
            migrationBuilder.Sql(@"
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='business_name') THEN
        ALTER TABLE users ADD COLUMN business_name VARCHAR(200);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='referral_code') THEN
        ALTER TABLE users ADD COLUMN referral_code VARCHAR(20);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='rating') THEN
        ALTER TABLE users ADD COLUMN rating DECIMAL(3,2);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='events_completed') THEN
        ALTER TABLE users ADD COLUMN events_completed INT NOT NULL DEFAULT 0;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='vendor_id') THEN
        ALTER TABLE users ADD COLUMN vendor_id UUID;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='discipline_score') THEN
        ALTER TABLE users ADD COLUMN discipline_score DECIMAL(5,2) NOT NULL DEFAULT 100.00;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='events_attended') THEN
        ALTER TABLE users ADD COLUMN events_attended INT NOT NULL DEFAULT 0;
    END IF;
END $$;");

            migrationBuilder.Sql(@"
CREATE TABLE IF NOT EXISTS roles (
    id          UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
    name        VARCHAR(50) NOT NULL,
    description VARCHAR(255),
    role_type   INT NOT NULL,
    is_system   BOOL NOT NULL DEFAULT false,
    created_at  TIMESTAMP NOT NULL DEFAULT now(),
    created_by  UUID, updated_at TIMESTAMP, updated_by UUID,
    is_deleted  BOOL NOT NULL DEFAULT false, deleted_at TIMESTAMP, deleted_by UUID
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_roles_role_type ON roles(role_type) WHERE is_deleted = false;");

            migrationBuilder.Sql(@"
CREATE TABLE IF NOT EXISTS permissions (
    id          UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
    name        VARCHAR(100) NOT NULL,
    resource    VARCHAR(50) NOT NULL,
    action      VARCHAR(50) NOT NULL,
    description VARCHAR(255),
    created_at  TIMESTAMP NOT NULL DEFAULT now(),
    created_by  UUID, updated_at TIMESTAMP, updated_by UUID,
    is_deleted  BOOL NOT NULL DEFAULT false, deleted_at TIMESTAMP, deleted_by UUID
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_permissions_name ON permissions(name) WHERE is_deleted = false;");

            migrationBuilder.Sql(@"
CREATE TABLE IF NOT EXISTS role_permissions (
    id            UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
    role_id       UUID NOT NULL REFERENCES roles(id) ON DELETE CASCADE,
    permission_id UUID NOT NULL REFERENCES permissions(id) ON DELETE CASCADE,
    is_granted    BOOL NOT NULL DEFAULT true,
    created_at    TIMESTAMP NOT NULL DEFAULT now(),
    created_by    UUID, updated_at TIMESTAMP, updated_by UUID,
    is_deleted    BOOL NOT NULL DEFAULT false, deleted_at TIMESTAMP, deleted_by UUID
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_rp_role_perm ON role_permissions(role_id, permission_id) WHERE is_deleted = false;");

            migrationBuilder.Sql(@"
CREATE TABLE IF NOT EXISTS user_role_permissions (
    id            UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
    user_id       UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    permission_id UUID NOT NULL REFERENCES permissions(id) ON DELETE CASCADE,
    is_granted    BOOL NOT NULL DEFAULT true,
    expires_at    TIMESTAMP,
    granted_by    UUID,
    created_at    TIMESTAMP NOT NULL DEFAULT now(),
    created_by    UUID, updated_at TIMESTAMP, updated_by UUID,
    is_deleted    BOOL NOT NULL DEFAULT false, deleted_at TIMESTAMP, deleted_by UUID
);");

            migrationBuilder.Sql(@"
CREATE TABLE IF NOT EXISTS manager_permissions (
    id            UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
    manager_id    UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    permission_id UUID NOT NULL REFERENCES permissions(id) ON DELETE CASCADE,
    is_active     BOOL NOT NULL DEFAULT true,
    expires_at    TIMESTAMP,
    granted_by    UUID,
    created_at    TIMESTAMP NOT NULL DEFAULT now(),
    created_by    UUID, updated_at TIMESTAMP, updated_by UUID,
    is_deleted    BOOL NOT NULL DEFAULT false, deleted_at TIMESTAMP, deleted_by UUID
);
CREATE INDEX IF NOT EXISTS ix_mp_manager ON manager_permissions(manager_id);");

            migrationBuilder.Sql(@"
CREATE TABLE IF NOT EXISTS otp_requests (
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
    created_by  UUID, updated_at TIMESTAMP, updated_by UUID,
    is_deleted  BOOL NOT NULL DEFAULT false, deleted_at TIMESTAMP, deleted_by UUID
);
CREATE INDEX IF NOT EXISTS ix_otp_mobile_status ON otp_requests(mobile, status);");

            migrationBuilder.Sql(@"
CREATE TABLE IF NOT EXISTS refresh_tokens (
    id          UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
    user_id     UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    token_hash  VARCHAR(255) NOT NULL,
    device_id   VARCHAR(255),
    expires_at  TIMESTAMP NOT NULL,
    revoked_at  TIMESTAMP,
    replaced_by VARCHAR(255),
    ip_address  VARCHAR(45),
    created_at  TIMESTAMP NOT NULL DEFAULT now(),
    created_by  UUID, updated_at TIMESTAMP, updated_by UUID,
    is_deleted  BOOL NOT NULL DEFAULT false, deleted_at TIMESTAMP, deleted_by UUID
);
CREATE INDEX IF NOT EXISTS ix_rt_token_hash ON refresh_tokens(token_hash);
CREATE INDEX IF NOT EXISTS ix_rt_user_id ON refresh_tokens(user_id);");

            migrationBuilder.Sql(@"
CREATE TABLE IF NOT EXISTS user_sessions (
    id           UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
    user_id      UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    device_id    VARCHAR(255),
    device_name  VARCHAR(200),
    ip_address   VARCHAR(45),
    user_agent   VARCHAR(500),
    is_active    BOOL NOT NULL DEFAULT true,
    last_seen_at TIMESTAMP NOT NULL,
    revoked_at   TIMESTAMP,
    revoked_by   UUID,
    created_at   TIMESTAMP NOT NULL DEFAULT now(),
    created_by   UUID, updated_at TIMESTAMP, updated_by UUID,
    is_deleted   BOOL NOT NULL DEFAULT false, deleted_at TIMESTAMP, deleted_by UUID
);
CREATE INDEX IF NOT EXISTS ix_us_user_id ON user_sessions(user_id);");

            migrationBuilder.Sql(@"
CREATE TABLE IF NOT EXISTS vendor_crew_mappings (
    id                     UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
    vendor_id              UUID NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
    crew_id                UUID NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
    approved_by_manager_id UUID NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
    is_active              BOOL NOT NULL DEFAULT true,
    mapped_at              TIMESTAMP NOT NULL,
    removed_at             TIMESTAMP,
    notes                  VARCHAR(500),
    created_at             TIMESTAMP NOT NULL DEFAULT now(),
    created_by             UUID, updated_at TIMESTAMP, updated_by UUID,
    is_deleted             BOOL NOT NULL DEFAULT false, deleted_at TIMESTAMP, deleted_by UUID
);
CREATE INDEX IF NOT EXISTS ix_vcm_vendor_id ON vendor_crew_mappings(vendor_id);
CREATE INDEX IF NOT EXISTS ix_vcm_crew_id ON vendor_crew_mappings(crew_id);
CREATE INDEX IF NOT EXISTS ix_vcm_vendor_crew_active ON vendor_crew_mappings(vendor_id, crew_id, is_active);");

            migrationBuilder.Sql(@"
CREATE TABLE IF NOT EXISTS audit_logs (
    id           UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
    actor_id     UUID,
    actor_mobile VARCHAR(20),
    action       VARCHAR(100) NOT NULL,
    entity_name  VARCHAR(100) NOT NULL,
    entity_id    VARCHAR(100),
    old_values   TEXT,
    new_values   TEXT,
    ip_address   VARCHAR(45),
    user_agent   VARCHAR(500),
    created_at   TIMESTAMP NOT NULL DEFAULT now(),
    created_by   UUID, updated_at TIMESTAMP, updated_by UUID,
    is_deleted   BOOL NOT NULL DEFAULT false, deleted_at TIMESTAMP, deleted_by UUID
);
CREATE INDEX IF NOT EXISTS ix_al_actor_id ON audit_logs(actor_id);
CREATE INDEX IF NOT EXISTS ix_al_entity_name ON audit_logs(entity_name);
CREATE INDEX IF NOT EXISTS ix_al_created_at ON audit_logs(created_at);");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE IF EXISTS audit_logs CASCADE;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS vendor_crew_mappings CASCADE;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS user_sessions CASCADE;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS refresh_tokens CASCADE;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS otp_requests CASCADE;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS manager_permissions CASCADE;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS user_role_permissions CASCADE;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS role_permissions CASCADE;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS permissions CASCADE;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS roles CASCADE;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS users CASCADE;");
        }
    }
}
