using EventWOS.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EventWOS.Persistence.Configurations;

public sealed class UserRolePermissionConfiguration : IEntityTypeConfiguration<UserRolePermission>
{
    public void Configure(EntityTypeBuilder<UserRolePermission> builder)
    {
        // Same missing-configuration bug as RolePermission/ManagerPermission -
        // EF defaulted to table "UserRolePermissions" (PascalCase, doesn't
        // exist); actual table is user_role_permissions.
        builder.ToTable("user_role_permissions");
        builder.HasKey(urp => urp.Id);
        builder.Property(urp => urp.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(urp => urp.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(urp => urp.PermissionId).HasColumnName("permission_id").IsRequired();
        builder.Property(urp => urp.IsGranted).HasColumnName("is_granted").HasDefaultValue(true);
        builder.Property(urp => urp.ExpiresAt).HasColumnName("expires_at");
        builder.Property(urp => urp.GrantedByAdminId).HasColumnName("granted_by");
        // "reason" did not exist as a column at all until migration
        // 20260818000100_AddUserRolePermissionReason (entity had the property,
        // table never did — added alongside this configuration fix).
        builder.Property(urp => urp.Reason).HasColumnName("reason").HasMaxLength(500);
        builder.Property(urp => urp.CreatedAt).HasColumnName("created_at");
        builder.Property(urp => urp.CreatedBy).HasColumnName("created_by");
        builder.Property(urp => urp.UpdatedAt).HasColumnName("updated_at");
        builder.Property(urp => urp.UpdatedBy).HasColumnName("updated_by");
        builder.Property(urp => urp.IsDeleted).HasColumnName("is_deleted").HasDefaultValue(false);
        builder.Property(urp => urp.DeletedAt).HasColumnName("deleted_at");
        builder.Property(urp => urp.DeletedBy).HasColumnName("deleted_by");

        builder.HasOne(urp => urp.User)
            .WithMany(u => u.RolePermissions)          // User.RolePermissions - must be named
            .HasForeignKey(urp => urp.UserId)          // explicitly (see RolePermissionConfiguration
            .OnDelete(DeleteBehavior.Cascade);         // for why a bare WithMany() is unsafe here).

        builder.HasOne(urp => urp.Permission)
            .WithMany(perm => perm.UserPermissions)    // Permission.UserPermissions
            .HasForeignKey(urp => urp.PermissionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(urp => new { urp.UserId, urp.PermissionId })
            .HasDatabaseName("ix_urp_user_perm");
    }
}
