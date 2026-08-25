using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventOpsOracle.Persistence.Migrations
{
    /// <summary>
    /// Aligns audit_logs' physical columns with what AuditLogConfiguration /
    /// the AuditLog entity have actually expected since AuditLogger was
    /// written: PerformedByUserId, PerformedByIp, EntityType, OccurredAt,
    /// AdditionalData. InitialCreate (20260525) created this table with an
    /// older, unrelated shape (actor_id, ip_address, entity_name — no
    /// occurred_at/additional_data at all) that was never updated, so every
    /// AuditLogger.LogAsync() call (login, logout, profile update, status
    /// change, ...) would have failed with "column ... does not exist" the
    /// first time it actually ran.
    ///
    /// Table is expected to be empty/near-empty on any DB hitting this
    /// migration for the first time, so plain RENAME (not add+backfill+drop)
    /// is safe and simplest. Idempotent: every step is catalog-guarded.
    /// </summary>
    [Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute(typeof(AppDbContext))]
    [Migration("20260818000000_FixAuditLogsSchema")]
    public partial class FixAuditLogsSchema : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.columns
                WHERE table_name='audit_logs' AND column_name='actor_id')
       AND NOT EXISTS (SELECT 1 FROM information_schema.columns
                WHERE table_name='audit_logs' AND column_name='performed_by_user_id') THEN
        ALTER TABLE audit_logs RENAME COLUMN actor_id TO performed_by_user_id;
    END IF;

    IF EXISTS (SELECT 1 FROM information_schema.columns
                WHERE table_name='audit_logs' AND column_name='ip_address')
       AND NOT EXISTS (SELECT 1 FROM information_schema.columns
                WHERE table_name='audit_logs' AND column_name='performed_by_ip') THEN
        ALTER TABLE audit_logs RENAME COLUMN ip_address TO performed_by_ip;
    END IF;

    IF EXISTS (SELECT 1 FROM information_schema.columns
                WHERE table_name='audit_logs' AND column_name='entity_name')
       AND NOT EXISTS (SELECT 1 FROM information_schema.columns
                WHERE table_name='audit_logs' AND column_name='entity_type') THEN
        ALTER TABLE audit_logs RENAME COLUMN entity_name TO entity_type;
    END IF;

    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                WHERE table_name='audit_logs' AND column_name='occurred_at') THEN
        ALTER TABLE audit_logs ADD COLUMN occurred_at TIMESTAMP NOT NULL DEFAULT NOW();
    END IF;

    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                WHERE table_name='audit_logs' AND column_name='additional_data') THEN
        ALTER TABLE audit_logs ADD COLUMN additional_data VARCHAR(500);
    END IF;

    -- actor_mobile / user_agent are unused by AuditLog today - left in place,
    -- harmless (nullable, never written).

    IF EXISTS (SELECT 1 FROM pg_indexes WHERE indexname='ix_al_actor_id')
       AND NOT EXISTS (SELECT 1 FROM pg_indexes WHERE indexname='ix_al_performed_by_user_id') THEN
        ALTER INDEX ix_al_actor_id RENAME TO ix_al_performed_by_user_id;
    END IF;

    IF EXISTS (SELECT 1 FROM pg_indexes WHERE indexname='ix_al_entity_name')
       AND NOT EXISTS (SELECT 1 FROM pg_indexes WHERE indexname='ix_al_entity_type') THEN
        ALTER INDEX ix_al_entity_name RENAME TO ix_al_entity_type;
    END IF;
END
$$;
");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
ALTER TABLE audit_logs DROP COLUMN IF EXISTS additional_data;
ALTER TABLE audit_logs DROP COLUMN IF EXISTS occurred_at;
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.columns
                WHERE table_name='audit_logs' AND column_name='entity_type') THEN
        ALTER TABLE audit_logs RENAME COLUMN entity_type TO entity_name;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns
                WHERE table_name='audit_logs' AND column_name='performed_by_ip') THEN
        ALTER TABLE audit_logs RENAME COLUMN performed_by_ip TO ip_address;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns
                WHERE table_name='audit_logs' AND column_name='performed_by_user_id') THEN
        ALTER TABLE audit_logs RENAME COLUMN performed_by_user_id TO actor_id;
    END IF;
END
$$;
");
        }
    }
}
