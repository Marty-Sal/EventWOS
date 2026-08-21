using EventWOS.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EventWOS.Persistence.Configurations;

/// <summary>Maps <see cref="TermsAndConditions"/> to the <c>terms_and_conditions</c> table.</summary>
public sealed class TermsAndConditionsConfiguration : IEntityTypeConfiguration<TermsAndConditions>
{
    public void Configure(EntityTypeBuilder<TermsAndConditions> builder)
    {
        builder.ToTable("terms_and_conditions");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(t => t.Audience).HasColumnName("audience").HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(t => t.Version).HasColumnName("version").IsRequired();
        // No HasMaxLength — rich-text HTML from the WYSIWYG editor, mapped
        // to Postgres' unbounded `text` (Npgsql's default for string
        // properties with no max length). The domain entity's SetContent
        // still enforces a generous 200,000-char sanity cap.
        builder.Property(t => t.Content).HasColumnName("content").IsRequired();

        builder.Property(t => t.CreatedAt).HasColumnName("created_at");
        builder.Property(t => t.CreatedBy).HasColumnName("created_by");
        builder.Property(t => t.UpdatedAt).HasColumnName("updated_at");
        builder.Property(t => t.UpdatedBy).HasColumnName("updated_by");
        builder.Property(t => t.IsDeleted).HasColumnName("is_deleted").HasDefaultValue(false);
        builder.Property(t => t.DeletedAt).HasColumnName("deleted_at");
        builder.Property(t => t.DeletedBy).HasColumnName("deleted_by");

        // One row per (Audience, Version) — the app never issues duplicate
        // versions, but this is a hard backstop.
        builder.HasIndex(t => new { t.Audience, t.Version }).IsUnique().HasDatabaseName("ux_terms_audience_version");

        builder.HasQueryFilter(t => !t.IsDeleted);
    }
}
