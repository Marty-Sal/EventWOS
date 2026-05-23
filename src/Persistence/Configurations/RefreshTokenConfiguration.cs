using EventWOS.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EventWOS.Persistence.Configurations;

public sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("refresh_tokens");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(r => r.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(r => r.TokenHash).HasColumnName("token_hash").HasMaxLength(255).IsRequired();
        builder.Property(r => r.DeviceId).HasColumnName("device_id").HasMaxLength(255).IsRequired();
        builder.Property(r => r.IpAddress).HasColumnName("ip_address").HasMaxLength(45).IsRequired();
        builder.Property(r => r.ExpiresAt).HasColumnName("expires_at").IsRequired();
        builder.Property(r => r.IsRevoked).HasColumnName("is_revoked").HasDefaultValue(false);
        builder.Property(r => r.RevokedAt).HasColumnName("revoked_at");
        builder.Property(r => r.ReplacedByTokenHash).HasColumnName("replaced_by_token_hash").HasMaxLength(255);
        builder.Property(r => r.RevokeReason).HasColumnName("revoke_reason").HasMaxLength(100);
        builder.Property(r => r.CreatedAt).HasColumnName("created_at");
        builder.Property(r => r.IsDeleted).HasColumnName("is_deleted").HasDefaultValue(false);

        builder.HasIndex(r => r.TokenHash).IsUnique().HasDatabaseName("ix_refresh_tokens_hash");
        builder.HasIndex(r => new { r.UserId, r.IsRevoked }).HasDatabaseName("ix_refresh_tokens_user_active");
    }
}
