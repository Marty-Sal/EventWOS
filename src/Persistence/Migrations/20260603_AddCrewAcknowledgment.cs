using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventWOS.Persistence.Migrations
{
    /// <summary>
    /// Adds the crew acknowledgement columns to crew_payments so crew can mark
    /// a Paid payment as Received or Pending (Payment & Settlement Lifecycle step 5).
    /// </summary>
    [Migration("20260603000100_AddCrewAcknowledgment")]
    public partial class AddCrewAcknowledgment : Migration
    {
        protected override void Up(MigrationBuilder mb)
        {
            mb.AddColumn<string>(
                name: "crew_acknowledgment",
                table: "crew_payments",
                type: "text",
                nullable: false,
                defaultValue: "None");

            mb.AddColumn<DateTime>(
                name: "acknowledged_at",
                table: "crew_payments",
                type: "timestamp with time zone",
                nullable: true);

            mb.AddColumn<string>(
                name: "acknowledgment_note",
                table: "crew_payments",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }

        protected override void Down(MigrationBuilder mb)
        {
            mb.DropColumn(name: "acknowledgment_note", table: "crew_payments");
            mb.DropColumn(name: "acknowledged_at",      table: "crew_payments");
            mb.DropColumn(name: "crew_acknowledgment",  table: "crew_payments");
        }
    }
}
