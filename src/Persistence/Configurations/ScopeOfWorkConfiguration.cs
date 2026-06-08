using EventWOS.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EventWOS.Persistence.Configurations;

/// <summary>
/// Maps <see cref="ScopeOfWork"/> to the <c>scope_of_work</c> table.
///
/// The unique-name guarantee is split into TWO indices because we want the
/// rule "unique among ACTIVE rows" — not "globally unique":
///   1. <c>ux_scope_of_work_name_active</c> — filtered unique index on name
///      where <c>is_deleted = false</c>. This is what enforces the rule.
///   2. <c>ix_scope_of_work_name</c> — plain index used by listing/search.
///
/// Filtered unique index pattern matches <c>ux_cgm_group_crew_active</c>
/// (see memory rule #25 on CrewGroups). Both Fluent and migration SQL
/// declare the same shape — belt-and-braces (memory rule #26).
///
/// Global query filter excludes archived rows by default. Admin "Show
/// archived" toggle uses <c>IgnoreQueryFilters()</c> in the list query.
/// </summary>
public sealed class ScopeOfWorkConfiguration : IEntityTypeConfiguration<ScopeOfWork>
{
    public void Configure(EntityTypeBuilder<ScopeOfWork> builder)
    {
        builder.ToTable("scope_of_work");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.Id)
               .HasColumnName("id")
               .HasDefaultValueSql("gen_random_uuid()");

        builder.Property(s => s.Name)
               .HasColumnName("name")
               .HasMaxLength(80)
               .IsRequired();

        builder.Property(s => s.Description)
               .HasColumnName("description")
               .HasMaxLength(500);

        builder.Property(s => s.CreatedByUserId).HasColumnName("created_by_user_id");
        builder.Property(s => s.CreatedAt).HasColumnName("created_at");
        builder.Property(s => s.CreatedBy).HasColumnName("created_by");
        builder.Property(s => s.UpdatedAt).HasColumnName("updated_at");
        builder.Property(s => s.UpdatedBy).HasColumnName("updated_by");
        builder.Property(s => s.IsDeleted).HasColumnName("is_deleted").HasDefaultValue(false);
        builder.Property(s => s.DeletedAt).HasColumnName("deleted_at");
        builder.Property(s => s.DeletedBy).HasColumnName("deleted_by");

        // Plain lookup index for search/sort.
        builder.HasIndex(s => s.Name).HasDatabaseName("ix_scope_of_work_name");

        // Soft-delete query filter — archived rows excluded by default.
        // List queries that need archived rows call IgnoreQueryFilters().
        builder.HasQueryFilter(s => !s.IsDeleted);

        // NOTE: the filtered unique index "ux_scope_of_work_name_active" is
        // declared in raw SQL in the migration (EF Core can't express
        // ILOWER()-based filtered unique indexes cleanly enough to be worth
        // it). Keep the SQL and this config in sync.
    }
}
