using EventWOS.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EventWOS.Persistence.Configurations;

/// <summary>
/// Maps <see cref="Venue"/> to the <c>venues</c> table. Same filtered
/// unique-name-among-active-rows pattern as ScopeOfWorkConfiguration —
/// see that file's doc comment for the full rationale.
/// </summary>
public sealed class VenueConfiguration : IEntityTypeConfiguration<Venue>
{
    public void Configure(EntityTypeBuilder<Venue> builder)
    {
        builder.ToTable("venues");
        builder.HasKey(v => v.Id);

        builder.Property(v => v.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");

        builder.Property(v => v.Name).HasColumnName("name").HasMaxLength(120).IsRequired();
        builder.Property(v => v.AddressLine1).HasColumnName("address_line1").HasMaxLength(200).IsRequired();
        builder.Property(v => v.AddressLine2).HasColumnName("address_line2").HasMaxLength(200);
        builder.Property(v => v.ShortAddress).HasColumnName("short_address").HasMaxLength(200);
        builder.Property(v => v.City).HasColumnName("city").HasMaxLength(200).IsRequired();
        builder.Property(v => v.State).HasColumnName("state").HasMaxLength(100);
        builder.Property(v => v.PostalCode).HasColumnName("postal_code").HasMaxLength(20);
        builder.Property(v => v.Country).HasColumnName("country").HasMaxLength(100);
        // numeric(9,6) rather than double precision: coordinates are
        // fixed-precision decimal data, and 6 dp (~11 cm) is finer than any
        // consumer GPS or geocoder delivers. Fixing the scale in the database
        // also stops "same" venue coordinates from differing in the 12th
        // decimal place depending on how they were written. Precision 9 leaves
        // room for 3 integer digits (-180..180).
        builder.Property(v => v.Latitude).HasColumnName("latitude").HasColumnType("numeric(9,6)");
        builder.Property(v => v.Longitude).HasColumnName("longitude").HasColumnType("numeric(9,6)");
        builder.Property(v => v.Notes).HasColumnName("notes").HasMaxLength(1000);

        builder.Property(v => v.CreatedByUserId).HasColumnName("created_by_user_id");
        builder.Property(v => v.CreatedAt).HasColumnName("created_at");
        builder.Property(v => v.CreatedBy).HasColumnName("created_by");
        builder.Property(v => v.UpdatedAt).HasColumnName("updated_at");
        builder.Property(v => v.UpdatedBy).HasColumnName("updated_by");
        builder.Property(v => v.IsDeleted).HasColumnName("is_deleted").HasDefaultValue(false);
        builder.Property(v => v.DeletedAt).HasColumnName("deleted_at");
        builder.Property(v => v.DeletedBy).HasColumnName("deleted_by");

        builder.HasIndex(v => v.Name).HasDatabaseName("ix_venues_name");
        builder.HasQueryFilter(v => !v.IsDeleted);

        // NOTE: filtered unique index "ux_venues_name_active" is declared in
        // raw SQL in the migration, same reasoning as scope_of_work.
    }
}
