using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventOpsOracle.Persistence.Migrations
{
    /// <summary>
    /// user_role_permissions never had a "reason" column, even though
    /// UserRolePermission.Reason has existed on the entity since it was
    /// written (SetReason()). Found while adding the entity's first-ever
    /// EF configuration (UserRolePermissionConfiguration) - previously the
    /// whole entity used EF's default PascalCase conventions and was never
    /// successfully saved at all, so this gap was invisible until now.
    /// </summary>
    [Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute(typeof(AppDbContext))]
    [Migration("20260818000100_AddUserRolePermissionReason")]
    public partial class AddUserRolePermissionReason : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "ALTER TABLE user_role_permissions ADD COLUMN IF NOT EXISTS reason VARCHAR(500);");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "ALTER TABLE user_role_permissions DROP COLUMN IF EXISTS reason;");
        }
    }
}
