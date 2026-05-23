using EventWOS.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EventWOS.Persistence.Configurations;

public sealed class UserSessionConfiguration : IEntityTypeConfiguration<UserSession>
{
    public void Configure(EntityTypeBuilder<UserSession> builder)
    {
        builder.ToTable("user_sessions");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(s => s.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(s => s.SessionId).HasColumnName("session_id").IsRequired();
        builder.Property(s => s.DeviceId).HasColumnName("device_id").HasMaxLength(255).IsRequired();
        builder.Property(s => s.DeviceName).HasColumnName("device_name").HasMaxLength(100).IsRequired();
        builder.Property(s => s.IpAddress).HasColumnName("ip_address").HasMaxLength(45).IsRequired();
        builder.Property(s => s.UserAgent).HasColumnName("user_agent").HasMaxLength(500).IsRequired();
        builder.Property(s => s.LastActivityAt).HasColumnName("last_activity_at").IsRequired();
        builder.Property(s => s.IsActive).HasColumnName("is_active").HasDefaultValue(true);
        builder.Property(s => s.TerminatedAt).HasColumnName("terminated_at");
        builder.Property(s => s.TerminationReason).HasColumnName("termination_reason").HasMaxLength(100);
        builder.Property(s => s.CreatedAt).HasColumnName("created_at");
        builder.Property(s => s.IsDeleted).HasColumnName("is_deleted").HasDefaultValue(false);

        builder.HasIndex(s => s.SessionId).IsUnique().HasDatabaseName("ix_user_sessions_session_id");
        builder.HasIndex(s => new { s.UserId, s.IsActive }).HasDatabaseName("ix_user_sessions_user_active");
    }
}
