using EventWOS.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EventWOS.Persistence.Configurations;

public sealed class PayrollBatchConfiguration : IEntityTypeConfiguration<PayrollBatch>
{
    public void Configure(EntityTypeBuilder<PayrollBatch> b)
    {
        b.ToTable("payroll_batches");
        b.HasKey(pb => pb.Id);

        // ── BaseEntity columns ────────────────────────────────────────────────
        b.Property(pb => pb.Id)        .HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        b.Property(pb => pb.CreatedAt) .HasColumnName("created_at");
        b.Property(pb => pb.CreatedBy) .HasColumnName("created_by");
        b.Property(pb => pb.UpdatedAt) .HasColumnName("updated_at");
        b.Property(pb => pb.UpdatedBy) .HasColumnName("updated_by");
        b.Property(pb => pb.IsDeleted) .HasColumnName("is_deleted").HasDefaultValue(false);
        b.Property(pb => pb.DeletedAt) .HasColumnName("deleted_at");
        b.Property(pb => pb.DeletedBy) .HasColumnName("deleted_by");

        // ── Domain columns ────────────────────────────────────────────────────
        b.Property(pb => pb.VendorId)           .HasColumnName("vendor_id");
        b.Property(pb => pb.EventId)            .HasColumnName("event_id");
        b.Property(pb => pb.BatchRef)           .HasColumnName("batch_ref").HasMaxLength(100).IsRequired();
        b.Property(pb => pb.Status)             .HasColumnName("status").HasConversion<string>().IsRequired();
        b.Property(pb => pb.TotalAmount)        .HasColumnName("total_amount").HasColumnType("numeric(14,2)");
        b.Property(pb => pb.Notes)              .HasColumnName("notes").HasMaxLength(1000);
        b.Property(pb => pb.SubmittedAt)        .HasColumnName("submitted_at");
        b.Property(pb => pb.ApprovedAt)         .HasColumnName("approved_at");
        b.Property(pb => pb.DisbursedAt)        .HasColumnName("disbursed_at");
        b.Property(pb => pb.ApprovedByUserId)   .HasColumnName("approved_by_user_id");

        // ── Relationships ─────────────────────────────────────────────────────
        b.HasOne(pb => pb.Vendor)
            .WithMany()
            .HasForeignKey(pb => pb.VendorId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasOne(pb => pb.Event)
            .WithMany()
            .HasForeignKey(pb => pb.EventId)
            .OnDelete(DeleteBehavior.Restrict);

        b.Navigation(pb => pb.Payments).AutoInclude(false);

        // ── Indexes ───────────────────────────────────────────────────────────
        b.HasIndex(pb => pb.VendorId) .HasDatabaseName("ix_payroll_batches_vendor_id");
        b.HasIndex(pb => pb.EventId)  .HasDatabaseName("ix_payroll_batches_event_id");
        b.HasIndex(pb => pb.Status)   .HasDatabaseName("ix_payroll_batches_status");
        b.HasIndex(pb => pb.BatchRef) .IsUnique().HasDatabaseName("ix_payroll_batches_batch_ref");
    }
}
