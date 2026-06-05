using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventWOS.Persistence.Migrations
{
    /// <summary>
    /// Adds attendance audit columns to event_assignments. Used when an Admin
    /// flips a hanging / no-show row to Attended after the event has completed
    /// — the note stores who did it and when, so vendors / crew / managers
    /// can see the override on every surface that renders the assignment.
    ///
    /// Uses raw IF NOT EXISTS SQL to match the idempotent pattern used by
    /// every other migration in this project. Safe to re-run.
    /// </summary>
    [Migration("20260606000100_AddAttendanceNote")]
    public partial class AddAttendanceNote : Migration
    {
        protected override void Up(MigrationBuilder mb)
        {
            mb.Sql(@"
ALTER TABLE event_assignments
    ADD COLUMN IF NOT EXISTS attendance_note            VARCHAR(500),
    ADD COLUMN IF NOT EXISTS attendance_note_at         TIMESTAMPTZ,
    ADD COLUMN IF NOT EXISTS attendance_note_by_user_id UUID;
");
        }

        protected override void Down(MigrationBuilder mb)
        {
            mb.Sql(@"
ALTER TABLE event_assignments
    DROP COLUMN IF EXISTS attendance_note,
    DROP COLUMN IF EXISTS attendance_note_at,
    DROP COLUMN IF EXISTS attendance_note_by_user_id;
");
        }
    }
}
