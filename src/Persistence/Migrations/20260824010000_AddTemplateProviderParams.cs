using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventOpsOracle.Persistence.Migrations;

/// <summary>
/// Adds notification_templates.provider_params: the ordered token names fed into
/// a provider template's numbered placeholders ({{1}}, {{2}} ...).
///
/// Kept out of the body text on purpose. The approved WhatsApp template lives at
/// the provider and our body lives here, so the mapping between them has to be
/// stated explicitly -- inferring it from token order would silently send the
/// venue where the date should be the moment the two drift apart.
/// </summary>
public partial class AddTemplateProviderParams : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE notification_templates
                ADD COLUMN IF NOT EXISTS provider_params VARCHAR(500);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE notification_templates DROP COLUMN IF EXISTS provider_params;
            """);
    }
}
