using EventWOS.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EventWOS.Persistence.Configurations;

public sealed class ManagerPermissionConfiguration : IEntityTypeConfiguration<ManagerPermission>
{
    public void Configure(EntityTypeBuilder<ManagerPermission> builder)
    {
        // Same missing-configuration bug as RolePermission/UserRolePermission -
        // EF defaulted to table "ManagerPermissions" (PascalCase, doesn't
        // exist); actual table is manager_permissions.
        builder.ToTable("manager_permissions");
        builder.HasKey(mp => mp.Id);
        builder.Property(mp => mp.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(mp => mp.ManagerId).HasColumnName("manager_id").IsRequired();
        builder.Property(mp => mp.PermissionId).HasColumnName("permission_id").IsRequired();
        builder.Property(mp => mp.GrantedByAdminId).HasColumnName("granted_by");
        builder.Property(mp => mp.IsActive).HasColumnName("is_active").HasDefaultValue(true);
        builder.Property(mp => mp.ExpiresAt).HasColumnName("expires_at");
        builder.Property(mp => mp.CreatedAt).HasColumnName("created_at");
        builder.Property(mp => mp.CreatedBy).HasColumnName("created_by");
        builder.Property(mp => mp.UpdatedAt).HasColumnName("updated_at");
        builder.Property(mp => mp.UpdatedBy).HasColumnName("updated_by");
        builder.Property(mp => mp.IsDeleted).HasColumnName("is_deleted").HasDefaultValue(false);
        builder.Property(mp => mp.DeletedAt).HasColumnName("deleted_at");
        builder.Property(mp => mp.DeletedBy).HasColumnName("deleted_by");

        builder.HasOne(mp => mp.Manager)
            .WithMany()
            .HasForeignKey(mp => mp.ManagerId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(mp => mp.Permission)
            .WithMany()
            .HasForeignKey(mp => mp.PermissionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(mp => mp.ManagerId).HasDatabaseName("ix_mp_manager");
    }
}
