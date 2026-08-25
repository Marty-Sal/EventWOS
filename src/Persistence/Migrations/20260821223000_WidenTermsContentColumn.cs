using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventOpsOracle.Persistence.Migrations
{
    /// <summary>
    /// Terms & Conditions content became rich-text HTML (WYSIWYG editor,
    /// see RichTextEditor.razor) instead of plain text — markup pushes the
    /// same visible document well past the original VARCHAR(20000) cap.
    /// Widens terms_and_conditions.content to unbounded TEXT.
    ///
    /// Idempotent: ALTER COLUMN ... TYPE text is a no-op when the column is
    /// already text, matching this project's migration style.
    /// </summary>
    [Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute(typeof(AppDbContext))]
    [Migration("20260821223000_WidenTermsContentColumn")]
    public partial class WidenTermsContentColumn : Migration
    {
        protected override void Up(MigrationBuilder mb)
        {
            mb.Sql(@"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'terms_and_conditions') THEN
        ALTER TABLE terms_and_conditions ALTER COLUMN content TYPE TEXT;
    END IF;
END $$;
");
        }

        protected override void Down(MigrationBuilder mb)
        {
            // Not reversible without risking truncation of existing rich content — no-op.
        }
    }
}
