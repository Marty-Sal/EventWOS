using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventWOS.Persistence.Migrations
{
    [Migration("20260603000300_CrewPaymentVendorNullable")]
    public partial class CrewPaymentVendorNullable : Migration
    {
        protected override void Up(MigrationBuilder mb)
        {
            mb.AlterColumn<System.Guid>(
                name: "vendor_id",
                table: "crew_payments",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(System.Guid),
                oldType: "uuid");
        }

        protected override void Down(MigrationBuilder mb)
        {
            mb.AlterColumn<System.Guid>(
                name: "vendor_id",
                table: "crew_payments",
                type: "uuid",
                nullable: false,
                defaultValue: System.Guid.Empty,
                oldClrType: typeof(System.Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
