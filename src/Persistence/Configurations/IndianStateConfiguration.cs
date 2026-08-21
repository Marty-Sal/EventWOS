using EventWOS.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EventWOS.Persistence.Configurations;

/// <summary>
/// Maps <see cref="IndianState"/> to the <c>indian_states</c> table.
/// Pure reference data — seeded once, read-only from the app's
/// perspective (no controller ever writes to this table).
/// </summary>
public sealed class IndianStateConfiguration : IEntityTypeConfiguration<IndianState>
{
    public void Configure(EntityTypeBuilder<IndianState> builder)
    {
        builder.ToTable("indian_states");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(s => s.Name).HasColumnName("name").HasMaxLength(100).IsRequired();
        builder.Property(s => s.IsUnionTerritory).HasColumnName("is_union_territory").HasDefaultValue(false);
        builder.Property(s => s.SortOrder).HasColumnName("sort_order");

        builder.Property(s => s.CreatedAt).HasColumnName("created_at");
        builder.Property(s => s.CreatedBy).HasColumnName("created_by");
        builder.Property(s => s.UpdatedAt).HasColumnName("updated_at");
        builder.Property(s => s.UpdatedBy).HasColumnName("updated_by");
        builder.Property(s => s.IsDeleted).HasColumnName("is_deleted").HasDefaultValue(false);
        builder.Property(s => s.DeletedAt).HasColumnName("deleted_at");
        builder.Property(s => s.DeletedBy).HasColumnName("deleted_by");

        builder.HasIndex(s => s.Name).IsUnique().HasDatabaseName("ux_indian_states_name");
        builder.HasQueryFilter(s => !s.IsDeleted);
    }
}
