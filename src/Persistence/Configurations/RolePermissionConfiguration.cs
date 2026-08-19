using EventWOS.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EventWOS.Persistence.Configurations;

public sealed class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> builder)
    {
        // No configuration existed for this entity at all - EF's default
        // convention mapped it to a PascalCase table "RolePermissions" with
        // PascalCase columns, none of which exist (the actual snake_case
        // table role_permissions was created by raw SQL in InitialCreate).
        // Every seed/permission-grant write failed with:
        //   42P01: relation "RolePermissions" does not exist
        builder.ToTable("role_permissions");
        builder.HasKey(rp => rp.Id);
        builder.Property(rp => rp.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(rp => rp.RoleId).HasColumnName("role_id").IsRequired();
        builder.Property(rp => rp.PermissionId).HasColumnName("permission_id").IsRequired();
        builder.Property(rp => rp.IsGranted).HasColumnName("is_granted").HasDefaultValue(true);
        builder.Property(rp => rp.CreatedAt).HasColumnName("created_at");
        builder.Property(rp => rp.CreatedBy).HasColumnName("created_by");
        builder.Property(rp => rp.UpdatedAt).HasColumnName("updated_at");
        builder.Property(rp => rp.UpdatedBy).HasColumnName("updated_by");
        builder.Property(rp => rp.IsDeleted).HasColumnName("is_deleted").HasDefaultValue(false);
        builder.Property(rp => rp.DeletedAt).HasColumnName("deleted_at");
        builder.Property(rp => rp.DeletedBy).HasColumnName("deleted_by");

        builder.HasOne(rp => rp.Role)
            .WithMany(r => r.Permissions)
            .HasForeignKey(rp => rp.RoleId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(rp => rp.Permission)
            .WithMany(perm => perm.RolePermissions)   // Permission.RolePermissions - must be
            .HasForeignKey(rp => rp.PermissionId)     // named explicitly, else EF's own convention
            .OnDelete(DeleteBehavior.Cascade);        // discovery pairs it separately and creates
                                                       // a duplicate shadow FK "PermissionId1".


        builder.HasIndex(rp => new { rp.RoleId, rp.PermissionId })
            .IsUnique()
            .HasDatabaseName("ix_rp_role_perm")
            .HasFilter("is_deleted = false");


        // Global soft-delete filter — matches every other entity in the project.
        // List queries that need archived/deleted rows call IgnoreQueryFilters().
        builder.HasQueryFilter(rp => !rp.IsDeleted);
    }
}
