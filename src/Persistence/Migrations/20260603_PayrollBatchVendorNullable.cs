using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventWOS.Persistence.Migrations
{
    /// <summary>
    /// Makes payroll_batches.vendor_id nullable so we can create batches for
    /// direct-assigned crew (no intermediary vendor) under the event-centric
    /// "New Payroll Batch" flow.
    /// </summary>
    [Migration("20260603000200_PayrollBatchVendorNullable")]
    public partial class PayrollBatchVendorNullable : Migration
    {
        protected override void Up(MigrationBuilder mb)
        {
            mb.AlterColumn<System.Guid>(
                name: "vendor_id",
                table: "payroll_batches",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(System.Guid),
                oldType: "uuid");
        }

        protected override void Down(MigrationBuilder mb)
        {
            mb.AlterColumn<System.Guid>(
                name: "vendor_id",
                table: "payroll_batches",
                type: "uuid",
                nullable: false,
                defaultValue: System.Guid.Empty,
                oldClrType: typeof(System.Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
