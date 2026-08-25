using EventOpsOracle.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EventOpsOracle.Persistence.Configurations;

/// <summary>Maps <see cref="TermsAcceptance"/> to the <c>terms_acceptances</c> table.</summary>
public sealed class TermsAcceptanceConfiguration : IEntityTypeConfiguration<TermsAcceptance>
{
    public void Configure(EntityTypeBuilder<TermsAcceptance> builder)
    {
        builder.ToTable("terms_acceptances");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(t => t.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(t => t.Audience).HasColumnName("audience").HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(t => t.Version).HasColumnName("version").IsRequired();

        builder.Property(t => t.CreatedAt).HasColumnName("created_at");
        builder.Property(t => t.CreatedBy).HasColumnName("created_by");
        builder.Property(t => t.UpdatedAt).HasColumnName("updated_at");
        builder.Property(t => t.UpdatedBy).HasColumnName("updated_by");
        builder.Property(t => t.IsDeleted).HasColumnName("is_deleted").HasDefaultValue(false);
        builder.Property(t => t.DeletedAt).HasColumnName("deleted_at");
        builder.Property(t => t.DeletedBy).HasColumnName("deleted_by");

        // Fast "has user X accepted current version of audience Y" lookups.
        builder.HasIndex(t => new { t.UserId, t.Audience, t.Version }).HasDatabaseName("ix_terms_acceptances_user_audience_version");

        // Relationship to users. The database has a real FK
        // (fk_terms_acceptances_user_id -> users.id, added by migration
        // 20260821214500_AddTermsAndConditions with ON DELETE CASCADE), but
        // until this mapping existed the EF model had no dependency edge
        // between TermsAcceptance and User -- UserId was just a loose Guid.
        //
        // That matters because self-registration (RegisterVendorHandler /
        // RegisterCrewHandler) adds the new User AND its acceptance row in a
        // single SaveChanges. With no modelled dependency, EF is free to order
        // the INSERTs however it likes, and it put terms_acceptances first ->
        // 23503 foreign key violation, so nobody could register. Being inside
        // one transaction does not help: FK checks here are immediate, not
        // deferred. The ordering has to be modelled, not assumed.
        //
        // No navigation property is exposed -- this entity stays a flat
        // append-only audit record; the mapping exists purely so EF sorts the
        // inserts correctly. Same shape as the existing self-reference in
        // UserConfiguration (users.invited_by_user_id).
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(t => t.UserId)
            .HasConstraintName("fk_terms_acceptances_user_id")
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasQueryFilter(t => !t.IsDeleted);
    }
}
