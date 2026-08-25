using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventOpsOracle.Persistence.Migrations
{
    /// <summary>
    /// Settings module: Terms &amp; Conditions. Two append-only tables —
    /// terms_and_conditions (version history per audience: Vendor/Crew) and
    /// terms_acceptances (audit trail of who accepted which version, when).
    ///
    /// Idempotent raw SQL, matching this project's migration style.
    /// </summary>
    [Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute(typeof(AppDbContext))]
    [Migration("20260821214500_AddTermsAndConditions")]
    public partial class AddTermsAndConditions : Migration
    {
        protected override void Up(MigrationBuilder mb)
        {
            mb.Sql(@"
CREATE TABLE IF NOT EXISTS terms_and_conditions (
    id           UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
    audience     VARCHAR(20) NOT NULL,
    version      INT NOT NULL,
    content      VARCHAR(20000) NOT NULL,
    created_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
    created_by   UUID,
    updated_at   TIMESTAMPTZ,
    updated_by   UUID,
    is_deleted   BOOLEAN NOT NULL DEFAULT false,
    deleted_at   TIMESTAMPTZ,
    deleted_by   UUID
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_terms_audience_version
    ON terms_and_conditions (audience, version);

CREATE TABLE IF NOT EXISTS terms_acceptances (
    id           UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
    user_id      UUID NOT NULL,
    audience     VARCHAR(20) NOT NULL,
    version      INT NOT NULL,
    created_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
    created_by   UUID,
    updated_at   TIMESTAMPTZ,
    updated_by   UUID,
    is_deleted   BOOLEAN NOT NULL DEFAULT false,
    deleted_at   TIMESTAMPTZ,
    deleted_by   UUID
);

CREATE INDEX IF NOT EXISTS ix_terms_acceptances_user_audience_version
    ON terms_acceptances (user_id, audience, version);

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE constraint_name = 'fk_terms_acceptances_user_id'
    ) THEN
        ALTER TABLE terms_acceptances
            ADD CONSTRAINT fk_terms_acceptances_user_id
            FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE;
    END IF;
END $$;
");
        }

        protected override void Down(MigrationBuilder mb)
        {
            mb.Sql(@"
                DROP TABLE IF EXISTS terms_acceptances;
                DROP TABLE IF EXISTS terms_and_conditions;
            ");
        }
    }
}
