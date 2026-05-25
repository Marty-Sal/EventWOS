using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventWOS.Persistence.Migrations
{
    [Migration("20260527000100_AddPayments")]
    public partial class AddPayments : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // PayrollBatch
            migrationBuilder.CreateTable(
                name: "payroll_batches",
                columns: table => new
                {
                    id                  = table.Column<Guid>(type: "uuid", nullable: false),
                    vendor_id           = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id            = table.Column<Guid>(type: "uuid", nullable: false),
                    batch_ref           = table.Column<string>(maxLength: 100, nullable: false),
                    status              = table.Column<string>(nullable: false, defaultValue: "Draft"),
                    total_amount        = table.Column<decimal>(type: "numeric(14,2)", nullable: false, defaultValue: 0m),
                    notes               = table.Column<string>(maxLength: 1000, nullable: true),
                    submitted_at        = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    approved_at         = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    disbursed_at        = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    approved_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_date        = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    updated_date        = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    created_by          = table.Column<string>(maxLength: 100, nullable: true),
                    is_deleted          = table.Column<bool>(nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payroll_batches", x => x.id);
                    table.ForeignKey(
                        name: "fk_payroll_batches_vendor",
                        column: x => x.vendor_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payroll_batches_event",
                        column: x => x.event_id,
                        principalTable: "events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex("ix_payroll_batches_vendor_id", "payroll_batches", "vendor_id");
            migrationBuilder.CreateIndex("ix_payroll_batches_event_id",  "payroll_batches", "event_id");
            migrationBuilder.CreateIndex("ix_payroll_batches_status",    "payroll_batches", "status");
            migrationBuilder.CreateIndex(
                name: "ix_payroll_batches_batch_ref",
                table: "payroll_batches",
                column: "batch_ref",
                unique: true);

            // CrewPayment
            migrationBuilder.CreateTable(
                name: "crew_payments",
                columns: table => new
                {
                    id              = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id        = table.Column<Guid>(type: "uuid", nullable: false),
                    assignment_id   = table.Column<Guid>(type: "uuid", nullable: false),
                    crew_id         = table.Column<Guid>(type: "uuid", nullable: false),
                    vendor_id       = table.Column<Guid>(type: "uuid", nullable: false),
                    agreed_amount   = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    paid_amount     = table.Column<decimal>(type: "numeric(12,2)", nullable: true),
                    status          = table.Column<string>(nullable: false, defaultValue: "Pending"),
                    method          = table.Column<string>(nullable: true),
                    transaction_ref = table.Column<string>(maxLength: 200, nullable: true),
                    notes           = table.Column<string>(maxLength: 1000, nullable: true),
                    paid_at         = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    payroll_batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_date    = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    updated_date    = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    created_by      = table.Column<string>(maxLength: 100, nullable: true),
                    is_deleted      = table.Column<bool>(nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_crew_payments", x => x.id);
                    table.ForeignKey(
                        name: "fk_crew_payments_event",
                        column: x => x.event_id,
                        principalTable: "events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_crew_payments_assignment",
                        column: x => x.assignment_id,
                        principalTable: "event_assignments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_crew_payments_crew",
                        column: x => x.crew_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_crew_payments_vendor",
                        column: x => x.vendor_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_crew_payments_batch",
                        column: x => x.payroll_batch_id,
                        principalTable: "payroll_batches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex("ix_crew_payments_event_id",         "crew_payments", "event_id");
            migrationBuilder.CreateIndex("ix_crew_payments_crew_id",          "crew_payments", "crew_id");
            migrationBuilder.CreateIndex("ix_crew_payments_vendor_id",        "crew_payments", "vendor_id");
            migrationBuilder.CreateIndex("ix_crew_payments_status",           "crew_payments", "status");
            migrationBuilder.CreateIndex("ix_crew_payments_payroll_batch_id", "crew_payments", "payroll_batch_id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable("crew_payments");
            migrationBuilder.DropTable("payroll_batches");
        }
    }
}
