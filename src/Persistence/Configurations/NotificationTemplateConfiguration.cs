using EventWOS.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EventWOS.Persistence.Configurations;

/// <summary>Maps <see cref="NotificationTemplate"/> to <c>notification_templates</c>.</summary>
public sealed class NotificationTemplateConfiguration : IEntityTypeConfiguration<NotificationTemplate>
{
    public void Configure(EntityTypeBuilder<NotificationTemplate> builder)
    {
        builder.ToTable("notification_templates");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(t => t.Code).HasColumnName("code").HasMaxLength(60).IsRequired();
        builder.Property(t => t.Channel).HasColumnName("channel").HasConversion<string>().HasMaxLength(15).IsRequired();
        builder.Property(t => t.Language).HasColumnName("language").HasMaxLength(10).IsRequired();
        builder.Property(t => t.Subject).HasColumnName("subject").HasMaxLength(300);
        // Unbounded text: email bodies are HTML, same treatment as terms content.
        builder.Property(t => t.Body).HasColumnName("body").IsRequired();
        builder.Property(t => t.ProviderTemplateId).HasColumnName("provider_template_id").HasMaxLength(200);
        builder.Property(t => t.ProviderParams).HasColumnName("provider_params").HasMaxLength(500);
        builder.Property(t => t.Version).HasColumnName("version").HasDefaultValue(1);
        builder.Property(t => t.IsActive).HasColumnName("is_active").HasDefaultValue(true);

        builder.Property(t => t.CreatedAt).HasColumnName("created_at");
        builder.Property(t => t.CreatedBy).HasColumnName("created_by");
        builder.Property(t => t.UpdatedAt).HasColumnName("updated_at");
        builder.Property(t => t.UpdatedBy).HasColumnName("updated_by");
        builder.Property(t => t.IsDeleted).HasColumnName("is_deleted").HasDefaultValue(false);
        builder.Property(t => t.DeletedAt).HasColumnName("deleted_at");
        builder.Property(t => t.DeletedBy).HasColumnName("deleted_by");

        // One template per code+channel+language. Channel selection reads this
        // table, so a duplicate row would mean an ambiguous -- or doubled -- send.
        builder.HasIndex(t => new { t.Code, t.Channel, t.Language })
               .IsUnique()
               .HasDatabaseName("ux_notification_templates_code_channel_lang");

        builder.HasQueryFilter(t => !t.IsDeleted);
    }
}
