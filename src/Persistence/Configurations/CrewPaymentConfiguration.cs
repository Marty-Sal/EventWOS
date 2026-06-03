using EventWOS.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EventWOS.Persistence.Configurations;

public sealed class CrewPaymentConfiguration : IEntityTypeConfiguration<CrewPayment>
{
    public void Configure(EntityTypeBuilder<CrewPayment> b)
    {
        b.ToTable("crew_payments");
        b.HasKey(p => p.Id);

        // ── BaseEntity columns ────────────────────────────────────────────────
        b.Property(p => p.Id)         .HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        b.Property(p => p.CreatedAt)  .HasColumnName("created_at");
        b.Property(p => p.CreatedBy)  .HasColumnName("created_by");
        b.Property(p => p.UpdatedAt)  .HasColumnName("updated_at");
        b.Property(p => p.UpdatedBy)  .HasColumnName("updated_by");
        b.Property(p => p.IsDeleted)  .HasColumnName("is_deleted").HasDefaultValue(false);
        b.Property(p => p.DeletedAt)  .HasColumnName("deleted_at");
        b.Property(p => p.DeletedBy)  .HasColumnName("deleted_by");

        // ── Domain columns ────────────────────────────────────────────────────
        b.Property(p => p.EventId)      .HasColumnName("event_id");
        b.Property(p => p.AssignmentId) .HasColumnName("assignment_id");
        b.Property(p => p.CrewId)       .HasColumnName("crew_id");
        b.Property(p => p.VendorId)     .HasColumnName("vendor_id");
        b.Property(p => p.AgreedAmount) .HasColumnName("agreed_amount").HasColumnType("numeric(12,2)").IsRequired();
        b.Property(p => p.PaidAmount)   .HasColumnName("paid_amount").HasColumnType("numeric(12,2)");
        b.Property(p => p.Status)       .HasColumnName("status").HasConversion<string>().IsRequired();
        b.Property(p => p.Method)       .HasColumnName("method").HasConversion<string>();
        b.Property(p => p.TransactionRef).HasColumnName("transaction_ref").HasMaxLength(200);
        b.Property(p => p.Notes)        .HasColumnName("notes").HasMaxLength(1000);
        b.Property(p => p.PaidAt)       .HasColumnName("paid_at");
        b.Property(p => p.PayrollBatchId).HasColumnName("payroll_batch_id");

        // Crew acknowledgement (Payment & Settlement Lifecycle step 5)
        b.Property(p => p.CrewAcknowledgment).HasColumnName("crew_acknowledgment")
            .HasConversion<string>().HasDefaultValue(EventWOS.Domain.Enums.PaymentAcknowledgment.None).IsRequired();
        b.Property(p => p.AcknowledgedAt)    .HasColumnName("acknowledged_at");
        b.Property(p => p.AcknowledgmentNote).HasColumnName("acknowledgment_note").HasMaxLength(500);

        // ── Relationships ─────────────────────────────────────────────────────
        b.HasOne(p => p.Event)
            .WithMany()
            .HasForeignKey(p => p.EventId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasOne(p => p.Assignment)
            .WithMany()
            .HasForeignKey(p => p.AssignmentId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasOne(p => p.Crew)
            .WithMany()
            .HasForeignKey(p => p.CrewId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasOne(p => p.Vendor)
            .WithMany()
            .HasForeignKey(p => p.VendorId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasOne(p => p.PayrollBatch)
            .WithMany(pb => pb.Payments)
            .HasForeignKey(p => p.PayrollBatchId)
            .OnDelete(DeleteBehavior.SetNull);

        // ── Indexes ───────────────────────────────────────────────────────────
        b.HasIndex(p => p.EventId)        .HasDatabaseName("ix_crew_payments_event_id");
        b.HasIndex(p => p.CrewId)         .HasDatabaseName("ix_crew_payments_crew_id");
        b.HasIndex(p => p.VendorId)       .HasDatabaseName("ix_crew_payments_vendor_id");
        b.HasIndex(p => p.Status)         .HasDatabaseName("ix_crew_payments_status");
        b.HasIndex(p => p.PayrollBatchId) .HasDatabaseName("ix_crew_payments_payroll_batch_id");
    }
}
