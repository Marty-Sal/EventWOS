using Microsoft.EntityFrameworkCore.Migrations;

namespace EventWOS.Persistence.Migrations;

/// <summary>
/// Migration: Adds 2-step approval columns to event_assignments table.
/// Adds new AssignmentStatus enum values (stored as int — no schema change needed for enum).
/// </summary>
public partial class TwoStepApproval : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Add crew_responded_at (renamed from confirmed_at usage, kept for backward compat)
        migrationBuilder.Sql(@"
            ALTER TABLE event_assignments
                ADD COLUMN IF NOT EXISTS crew_responded_at    TIMESTAMPTZ,
                ADD COLUMN IF NOT EXISTS vendor_reviewed_at   TIMESTAMPTZ,
                ADD COLUMN IF NOT EXISTS manager_reviewed_at  TIMESTAMPTZ,
                ADD COLUMN IF NOT EXISTS rejection_reason     TEXT,
                ADD COLUMN IF NOT EXISTS rejected_by_user_id  UUID;

            CREATE INDEX IF NOT EXISTS ix_event_assignments_status
                ON event_assignments(status);

            CREATE INDEX IF NOT EXISTS ix_event_assignments_vendor_id
                ON event_assignments(vendor_id);
        ");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(@"
            ALTER TABLE event_assignments
                DROP COLUMN IF EXISTS crew_responded_at,
                DROP COLUMN IF EXISTS vendor_reviewed_at,
                DROP COLUMN IF EXISTS manager_reviewed_at,
                DROP COLUMN IF EXISTS rejection_reason,
                DROP COLUMN IF EXISTS rejected_by_user_id;
        ");
    }
}
