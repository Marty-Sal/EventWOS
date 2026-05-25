using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventWOS.Persistence.Migrations
{
    /// <summary>
    /// Fixes otp_requests: live DB has 'hashed_otp' (old name) AND a newly-added
    /// blank 'otp_hash' column from the startup patch. This migration copies the
    /// data across, drops hashed_otp, and ensures all columns are present.
    /// Fully idempotent.
    /// </summary>
    [Migration("20260528000000_FixOtpRequestsSchema")]
    public partial class FixOtpRequestsSchema : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DO $$
BEGIN
    -- Case A: Both hashed_otp AND otp_hash exist (previous patch added otp_hash as blank column)
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='otp_requests' AND column_name='hashed_otp')
       AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='otp_requests' AND column_name='otp_hash') THEN
        ALTER TABLE otp_requests ALTER COLUMN otp_hash DROP NOT NULL;
        UPDATE otp_requests SET otp_hash = hashed_otp WHERE otp_hash IS NULL OR otp_hash = '';
        ALTER TABLE otp_requests DROP COLUMN hashed_otp;
        ALTER TABLE otp_requests ALTER COLUMN otp_hash SET NOT NULL;
    END IF;

    -- Case B: Only hashed_otp exists (rename it)
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='otp_requests' AND column_name='hashed_otp')
       AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='otp_requests' AND column_name='otp_hash') THEN
        ALTER TABLE otp_requests RENAME COLUMN hashed_otp TO otp_hash;
    END IF;

    -- Ensure remaining columns exist
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='otp_requests' AND column_name='otp_hash') THEN
        ALTER TABLE otp_requests ADD COLUMN otp_hash VARCHAR(255) NOT NULL DEFAULT '';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='otp_requests' AND column_name='user_agent') THEN
        ALTER TABLE otp_requests ADD COLUMN user_agent VARCHAR(500);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='otp_requests' AND column_name='ip_address') THEN
        ALTER TABLE otp_requests ADD COLUMN ip_address VARCHAR(45);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='otp_requests' AND column_name='attempts') THEN
        ALTER TABLE otp_requests ADD COLUMN attempts INT NOT NULL DEFAULT 0;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='otp_requests' AND column_name='verified_at') THEN
        ALTER TABLE otp_requests ADD COLUMN verified_at TIMESTAMP;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='otp_requests' AND column_name='created_by') THEN
        ALTER TABLE otp_requests ADD COLUMN created_by UUID;
    END IF;
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
END $$;
");
        }

        protected override void Down(MigrationBuilder migrationBuilder) { }
    }
}
