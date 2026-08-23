using EventWOS.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EventWOS.Persistence.Configurations;

/// <summary>Maps <see cref="DeviceRegistration"/> to the <c>device_registrations</c> table.</summary>
public sealed class DeviceRegistrationConfiguration : IEntityTypeConfiguration<DeviceRegistration>
{
    public void Configure(EntityTypeBuilder<DeviceRegistration> builder)
    {
        builder.ToTable("device_registrations");
        builder.HasKey(d => d.Id);

        builder.Property(d => d.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(d => d.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(d => d.Provider).HasColumnName("provider").HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.Property(d => d.Endpoint).HasColumnName("endpoint").HasMaxLength(DeviceRegistration.MaxEndpointLength);
        builder.Property(d => d.P256dhKey).HasColumnName("p256dh_key").HasMaxLength(200);
        builder.Property(d => d.AuthSecret).HasColumnName("auth_secret").HasMaxLength(100);
        builder.Property(d => d.PushToken).HasColumnName("push_token").HasMaxLength(500);

        builder.Property(d => d.DeviceId).HasColumnName("device_id").HasMaxLength(100);
        builder.Property(d => d.Platform).HasColumnName("platform").HasMaxLength(40);
        builder.Property(d => d.UserAgent).HasColumnName("user_agent").HasMaxLength(400);

        builder.Property(d => d.IsActive).HasColumnName("is_active").HasDefaultValue(true);
        builder.Property(d => d.LastSeenAt).HasColumnName("last_seen_at");
        builder.Property(d => d.LastSuccessAt).HasColumnName("last_success_at");
        builder.Property(d => d.DeactivatedAt).HasColumnName("deactivated_at");
        builder.Property(d => d.DeactivationReason).HasColumnName("deactivation_reason").HasMaxLength(200);
        builder.Property(d => d.ConsecutiveFailures).HasColumnName("consecutive_failures").HasDefaultValue(0);

        builder.Property(d => d.CreatedAt).HasColumnName("created_at");
        builder.Property(d => d.CreatedBy).HasColumnName("created_by");
        builder.Property(d => d.UpdatedAt).HasColumnName("updated_at");
        builder.Property(d => d.UpdatedBy).HasColumnName("updated_by");
        builder.Property(d => d.IsDeleted).HasColumnName("is_deleted").HasDefaultValue(false);
        builder.Property(d => d.DeletedAt).HasColumnName("deleted_at");
        builder.Property(d => d.DeletedBy).HasColumnName("deleted_by");

        // The endpoint IS the subscription. A browser handing us the same endpoint
        // twice is the same device, so this index turns a caller's mistake into a
        // constraint violation instead of a duplicate push.
        //
        // Filtered on is_deleted because a soft-deleted row keeps its endpoint,
        // and without the filter that tombstone would permanently lock the same
        // browser out of ever re-subscribing.
        builder.HasIndex(d => d.Endpoint)
               .IsUnique()
               .HasFilter("is_deleted = false")
               .HasDatabaseName("ux_device_registrations_endpoint");

        builder.HasIndex(d => d.PushToken)
               .IsUnique()
               .HasFilter("is_deleted = false")
               .HasDatabaseName("ux_device_registrations_push_token");

        // The worker's only read: "every live subscription for this recipient".
        builder.HasIndex(d => new { d.UserId, d.IsActive })
               .HasDatabaseName("ix_device_registrations_user_active");

        // Real EF relationship, not a loose Guid. Nothing today writes a device
        // registration in the same SaveChanges as its user, but that is exactly
        // what was true of terms_acceptances before it took registration down
        // with a 23503 -- the mapping has to be modelled, not assumed. No
        // navigation property: this stays a flat table the worker reads.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(d => d.UserId)
            .HasConstraintName("fk_device_registrations_user_id")
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasQueryFilter(d => !d.IsDeleted);
    }
}
