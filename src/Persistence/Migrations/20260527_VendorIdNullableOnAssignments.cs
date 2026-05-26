using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventWOS.Persistence.Migrations
{
    /// <summary>
    /// Makes event_assignments.vendor_id AND event_assignments.crew_id nullable,
    /// to support 3 assignment modes from Manager/Admin:
    ///   1. Crew-only    (vendor_id NULL, crew_id set)     — direct assignment, skips vendor step
    ///   2. Vendor-only  (vendor_id set, crew_id NULL)     — vendor will fill in their own crew
    ///   3. Vendor+Crew  (both set)                         — original flow
    ///
    /// Fully idempotent — safe to run multiple times.
    /// </summary>
    [Migration("20260527120000_VendorIdNullableOnAssignments")]
    public partial class VendorIdNullableOnAssignments : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.columns
         WHERE table_name='event_assignments' AND column_name='vendor_id' AND is_nullable='NO'
    ) THEN
        ALTER TABLE event_assignments ALTER COLUMN vendor_id DROP NOT NULL;
        RAISE NOTICE 'event_assignments.vendor_id is now nullable';
    END IF;

    IF EXISTS (
        SELECT 1 FROM information_schema.columns
         WHERE table_name='event_assignments' AND column_name='crew_id' AND is_nullable='NO'
    ) THEN
        ALTER TABLE event_assignments ALTER COLUMN crew_id DROP NOT NULL;
        RAISE NOTICE 'event_assignments.crew_id is now nullable';
    END IF;
END
$$;
");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
ALTER TABLE event_assignments ALTER COLUMN vendor_id SET NOT NULL;
ALTER TABLE event_assignments ALTER COLUMN crew_id   SET NOT NULL;
");
        }
    }
}
