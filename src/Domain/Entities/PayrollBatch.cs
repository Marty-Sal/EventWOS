using EventWOS.Domain.Common;
using EventWOS.Domain.Enums;

namespace EventWOS.Domain.Entities;

/// <summary>
/// A vendor-level payroll batch grouping multiple crew payments for bulk disbursement.
/// </summary>
public sealed class PayrollBatch : BaseEntity
{
    private PayrollBatch() { }

    public PayrollBatch(
        Guid    vendorId,
        Guid    eventId,
        string  batchRef,
        string? notes = null)
    {
        VendorId  = vendorId;
        EventId   = eventId;
        BatchRef  = batchRef;
        Status    = PayrollStatus.Draft;
        Notes     = notes;
    }

    public Guid          VendorId       { get; private set; }
    public Guid          EventId        { get; private set; }
    public string        BatchRef       { get; private set; } = default!;
    public PayrollStatus Status         { get; private set; }
    public decimal       TotalAmount    { get; private set; }
    public string?       Notes          { get; private set; }
    public DateTime?     SubmittedAt    { get; private set; }
    public DateTime?     ApprovedAt     { get; private set; }
    public DateTime?     DisbursedAt    { get; private set; }
    public Guid?         ApprovedByUserId { get; private set; }

    // Navigation
    public User                        Vendor   { get; private set; } = default!;
    public Event                       Event    { get; private set; } = default!;
    public ICollection<CrewPayment>    Payments { get; private set; } = new List<CrewPayment>();

    public void RecalculateTotal()
        => TotalAmount = Payments.Where(p => p.Status != PaymentStatus.Rejected).Sum(p => p.AgreedAmount);

    public void Submit()
    {
        if (Status != PayrollStatus.Draft)
            throw new InvalidOperationException("Only Draft batches can be submitted.");
        Status      = PayrollStatus.Submitted;
        SubmittedAt = DateTime.UtcNow;
    }

    public void Approve(Guid approvedBy)
    {
        if (Status != PayrollStatus.Submitted)
            throw new InvalidOperationException("Only Submitted batches can be approved.");
        Status            = PayrollStatus.Approved;
        ApprovedAt        = DateTime.UtcNow;
        ApprovedByUserId  = approvedBy;
    }

    public void Disburse()
    {
        if (Status != PayrollStatus.Approved)
            throw new InvalidOperationException("Only Approved batches can be disbursed.");
        Status       = PayrollStatus.Disbursed;
        DisbursedAt  = DateTime.UtcNow;
    }

    public void Reject(string reason)
    {
        Status = PayrollStatus.Rejected;
        Notes  = reason;
    }
}
