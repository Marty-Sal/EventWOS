using EventWOS.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EventWOS.Persistence.Configurations;

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");

        builder.HasKey(u => u.Id);
        builder.Property(u => u.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");

        // Core
        builder.Property(u => u.Mobile).HasColumnName("mobile").HasMaxLength(20).IsRequired();
        builder.Property(u => u.FullName).HasColumnName("full_name").HasMaxLength(100).IsRequired();
        builder.Property(u => u.Email).HasColumnName("email").HasMaxLength(255);
        builder.Property(u => u.AvatarUrl).HasColumnName("avatar_url").HasMaxLength(500);
        builder.Property(u => u.Role).HasColumnName("role").IsRequired();
        builder.Property(u => u.Status).HasColumnName("status").IsRequired();
        builder.Property(u => u.ManagerId).HasColumnName("manager_id");
        builder.Property(u => u.DeviceId).HasColumnName("device_id").HasMaxLength(255);
        builder.Property(u => u.LastKnownIp).HasColumnName("last_known_ip").HasMaxLength(45);
        builder.Property(u => u.LastLoginAt).HasColumnName("last_login_at");
        builder.Property(u => u.FailedOtpAttempts).HasColumnName("failed_otp_attempts").HasDefaultValue(0);
        builder.Property(u => u.LockedUntil).HasColumnName("locked_until");

        // Auth (password-based)
        builder.Property(u => u.Username).HasColumnName("username").HasMaxLength(50);
        builder.Property(u => u.PasswordHash).HasColumnName("password_hash").HasMaxLength(255);
        builder.Property(u => u.RequirePasswordReset).HasColumnName("require_password_reset").HasDefaultValue(false);
        builder.Property(u => u.FailedLoginAttempts).HasColumnName("failed_login_attempts").HasDefaultValue(0);
        builder.Property(u => u.LastPasswordChangeAt).HasColumnName("last_password_change_at");

        // Self-registration: rejection + approval audit
        builder.Property(u => u.RejectedAt).HasColumnName("rejected_at");
        builder.Property(u => u.RejectionReason).HasColumnName("rejection_reason").HasMaxLength(500);
        builder.Property(u => u.RejectedByUserId).HasColumnName("rejected_by_user_id");
        builder.Property(u => u.ApprovedAt).HasColumnName("approved_at");
        builder.Property(u => u.ApprovedByUserId).HasColumnName("approved_by_user_id");

        // Extended profile (Vendor + Crew)
        builder.Property(u => u.ContactPersonName).HasColumnName("contact_person_name").HasMaxLength(150);
        builder.Property(u => u.GstNumber).HasColumnName("gst_number").HasMaxLength(50);
        builder.Property(u => u.Address).HasColumnName("address").HasMaxLength(500);
        builder.Property(u => u.City).HasColumnName("city").HasMaxLength(100);
        builder.Property(u => u.State).HasColumnName("state").HasMaxLength(100);
        builder.Property(u => u.Website).HasColumnName("website").HasMaxLength(255);
        builder.Property(u => u.Bio).HasColumnName("bio").HasMaxLength(2000);
        builder.Property(u => u.Skills).HasColumnName("skills").HasMaxLength(500);
        builder.Property(u => u.ExperienceYears).HasColumnName("experience_years");
        builder.Property(u => u.ReferralCodeUsed).HasColumnName("referral_code_used").HasMaxLength(20);

        // Vendor-specific
        builder.Property(u => u.BusinessName).HasColumnName("business_name").HasMaxLength(200);
        builder.Property(u => u.ReferralCode).HasColumnName("referral_code").HasMaxLength(20);
        builder.Property(u => u.Rating).HasColumnName("rating").HasPrecision(3, 2);
        builder.Property(u => u.EventsCompleted).HasColumnName("events_completed").HasDefaultValue(0);

        // Crew-specific
        builder.Property(u => u.VendorId).HasColumnName("vendor_id");
        builder.Property(u => u.DisciplineScore).HasColumnName("discipline_score").HasPrecision(5, 2).HasDefaultValue(100m);
        builder.Property(u => u.EventsAttended).HasColumnName("events_attended").HasDefaultValue(0);
        builder.Property(u => u.CrewRating).HasColumnName("crew_rating").HasPrecision(4, 2);
        builder.Property(u => u.CrewRatingCount).HasColumnName("crew_rating_count").HasDefaultValue(0);

        // Audit
        builder.Property(u => u.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        builder.Property(u => u.CreatedBy).HasColumnName("created_by");
        builder.Property(u => u.UpdatedAt).HasColumnName("updated_at");
        builder.Property(u => u.UpdatedBy).HasColumnName("updated_by");
        builder.Property(u => u.IsDeleted).HasColumnName("is_deleted").HasDefaultValue(false);
        builder.Property(u => u.DeletedAt).HasColumnName("deleted_at");
        builder.Property(u => u.DeletedBy).HasColumnName("deleted_by");

        // Indexes
        builder.HasIndex(u => u.Mobile).IsUnique().HasDatabaseName("ix_users_mobile");
        builder.HasIndex(u => u.Email).HasDatabaseName("ix_users_email");
        builder.HasIndex(u => u.Role).HasDatabaseName("ix_users_role");
        builder.HasIndex(u => u.Status).HasDatabaseName("ix_users_status");
        builder.HasIndex(u => u.ReferralCode).IsUnique().HasFilter("referral_code IS NOT NULL")
            .HasDatabaseName("ix_users_referral_code");
        builder.HasIndex(u => u.VendorId).HasDatabaseName("ix_users_vendor_id");
        builder.HasIndex(u => new { u.IsDeleted, u.Status }).HasDatabaseName("ix_users_soft_delete_status");
        builder.HasIndex(u => u.Username).IsUnique().HasFilter("username IS NOT NULL")
            .HasDatabaseName("ix_users_username");
        builder.HasIndex(u => u.RejectedAt).HasFilter("rejected_at IS NOT NULL")
            .HasDatabaseName("ix_users_rejected_at");

        // Relationships
        builder.HasOne(u => u.Manager)
            .WithMany()
            .HasForeignKey(u => u.ManagerId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(u => u.Vendor)
            .WithMany()
            .HasForeignKey(u => u.VendorId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasMany(u => u.RefreshTokens)
            .WithOne(r => r.User)
            .HasForeignKey(r => r.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(u => u.Sessions)
            .WithOne(s => s.User)
            .HasForeignKey(s => s.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
