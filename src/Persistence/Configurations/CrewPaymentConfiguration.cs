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

        b.Property(p => p.AgreedAmount).HasColumnType("numeric(12,2)").IsRequired();
        b.Property(p => p.PaidAmount).HasColumnType("numeric(12,2)");
        b.Property(p => p.Status).HasConversion<string>().IsRequired();
        b.Property(p => p.Method).HasConversion<string>();
        b.Property(p => p.TransactionRef).HasMaxLength(200);
        b.Property(p => p.Notes).HasMaxLength(1000);

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
            .OnDelete(DeleteBehavior.Restrict);

        b.HasOne(p => p.PayrollBatch)
            .WithMany(pb => pb.Payments)
            .HasForeignKey(p => p.PayrollBatchId)
            .OnDelete(DeleteBehavior.SetNull);

        b.HasIndex(p => p.EventId);
        b.HasIndex(p => p.CrewId);
        b.HasIndex(p => p.VendorId);
        b.HasIndex(p => p.Status);
        b.HasIndex(p => p.PayrollBatchId);
    }
}
