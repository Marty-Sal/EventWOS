using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventWOS.Persistence.Migrations
{
    /// <summary>
    /// Phase C of the Scope-of-Work feature: introduces
    /// <c>vendor_shift_allocations</c> — the per-vendor quota table that
    /// gates how many crew a vendor can invite onto a given shift.
    ///
    /// No backfill required. Phase B left every existing assignment pointed
    /// at the "General" shift on its event, with no vendor allocations.
    /// That's the correct starting state for legacy events: an admin will
    /// add vendor allocations going forward, and the assignment handlers
    /// fall back to the unallocated-vendor path for already-existing rows
    /// (see VendorAssignCrewHandler — opt-in quota enforcement keyed on
    /// "does an allocation row exist for this (shift, vendor)?").
    ///
    /// Idempotent raw SQL — safe to re-run on a DB that already has the
    /// table (e.g. Program.cs schema-patch ran first on cold start).
    /// </summary>
    public partial class AddVendorShiftAllocations : Migration
    {
        protected override void Up(MigrationBuilder mb)
        {
            mb.Sql(@"
                CREATE TABLE IF NOT EXISTS vendor_shift_allocations (
                    id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
                    shift_id           uuid NOT NULL REFERENCES event_shifts(id) ON DELETE CASCADE,
                    vendor_id          uuid NOT NULL REFERENCES users(id)        ON DELETE RESTRICT,
                    quota              integer NOT NULL CHECK (quota >= 1),
                    created_by_user_id uuid NOT NULL,

                    created_at         timestamptz NOT NULL DEFAULT now(),
                    created_by         uuid,
                    updated_at         timestamptz,
                    updated_by         uuid,
                    is_deleted         boolean NOT NULL DEFAULT false,
                    deleted_at         timestamptz,
                    deleted_by         uuid
                );

                -- Filtered unique: a vendor gets ONE active allocation per
                -- shift; archived rows free the slot. Same pattern as
                -- scope_of_work name index and crew_group_members.
                CREATE UNIQUE INDEX IF NOT EXISTS ux_vendor_shift_allocations_shift_vendor_active
                    ON vendor_shift_allocations (shift_id, vendor_id)
                    WHERE is_deleted = false;

                CREATE INDEX IF NOT EXISTS ix_vendor_shift_allocations_vendor_id
                    ON vendor_shift_allocations (vendor_id);
            ");
        }

        protected override void Down(MigrationBuilder mb)
        {
            mb.Sql(@"
                DROP INDEX IF EXISTS ix_vendor_shift_allocations_vendor_id;
                DROP INDEX IF EXISTS ux_vendor_shift_allocations_shift_vendor_active;
                DROP TABLE IF EXISTS vendor_shift_allocations;
            ");
        }
    }
}
