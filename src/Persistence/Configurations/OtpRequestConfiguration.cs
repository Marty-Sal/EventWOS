using EventWOS.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EventWOS.Persistence.Configurations;

public sealed class OtpRequestConfiguration : IEntityTypeConfiguration<OtpRequest>
{
    public void Configure(EntityTypeBuilder<OtpRequest> builder)
    {
        builder.ToTable("otp_requests");
        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(o => o.Mobile).HasColumnName("mobile").HasMaxLength(20).IsRequired();
        // DB column is otp_hash (matches migration SQL)
        builder.Property(o => o.HashedOtp).HasColumnName("otp_hash").HasMaxLength(255).IsRequired();
        // DB column is user_agent; mapped to DeviceId (both capture client identity)
        builder.Property(o => o.DeviceId).HasColumnName("user_agent").HasMaxLength(500);
        builder.Property(o => o.IpAddress).HasColumnName("ip_address").HasMaxLength(45);
        builder.Property(o => o.ExpiresAt).HasColumnName("expires_at").IsRequired();
        builder.Property(o => o.Status).HasColumnName("status").IsRequired();
        // DB column is attempts (matches migration SQL)
        builder.Property(o => o.AttemptCount).HasColumnName("attempts").HasDefaultValue(0);
        builder.Property(o => o.VerifiedAt).HasColumnName("verified_at");
        builder.Property(o => o.CreatedAt).HasColumnName("created_at");
        builder.Property(o => o.CreatedBy).HasColumnName("created_by");
        builder.Property(o => o.UpdatedAt).HasColumnName("updated_at");
        builder.Property(o => o.UpdatedBy).HasColumnName("updated_by");
        builder.Property(o => o.IsDeleted).HasColumnName("is_deleted").HasDefaultValue(false);
        builder.Property(o => o.DeletedAt).HasColumnName("deleted_at");
        builder.Property(o => o.DeletedBy).HasColumnName("deleted_by");

        builder.HasIndex(o => o.Mobile).HasDatabaseName("ix_otp_requests_mobile");
        builder.HasIndex(o => o.ExpiresAt).HasDatabaseName("ix_otp_requests_expires");
        builder.HasIndex(o => new { o.Mobile, o.Status }).HasDatabaseName("ix_otp_requests_mobile_status");
    }
}
