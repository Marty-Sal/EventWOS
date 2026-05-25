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

        b.Property(pb => pb.BatchRef).HasMaxLength(100).IsRequired();
        b.Property(pb => pb.Status).HasConversion<string>().IsRequired();
        b.Property(pb => pb.TotalAmount).HasColumnType("numeric(14,2)");
        b.Property(pb => pb.Notes).HasMaxLength(1000);

        b.HasOne(pb => pb.Vendor)
            .WithMany()
            .HasForeignKey(pb => pb.VendorId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasOne(pb => pb.Event)
            .WithMany()
            .HasForeignKey(pb => pb.EventId)
            .OnDelete(DeleteBehavior.Restrict);

        // Payments collection already configured from CrewPaymentConfiguration
        b.Navigation(pb => pb.Payments).AutoInclude(false);

        b.HasIndex(pb => pb.VendorId);
        b.HasIndex(pb => pb.EventId);
        b.HasIndex(pb => pb.Status);
        b.HasIndex(pb => pb.BatchRef).IsUnique();
    }
}
