using EventWOS.Domain.Common;
using EventWOS.Domain.Enums;

namespace EventWOS.Domain.Entities;

/// <summary>
/// Tracks the payment due/made to a single crew member for a specific event assignment.
/// </summary>
public sealed class CrewPayment : BaseEntity
{
    private CrewPayment() { }

    public CrewPayment(
        Guid   eventId,
        Guid   assignmentId,
        Guid   crewId,
        Guid   vendorId,
        decimal agreedAmount,
        string? notes = null)
    {
        EventId        = eventId;
        AssignmentId   = assignmentId;
        CrewId         = crewId;
        VendorId       = vendorId;
        AgreedAmount   = agreedAmount;
        Status         = PaymentStatus.Pending;
        Notes          = notes;
    }

    public Guid          EventId       { get; private set; }
    public Guid          AssignmentId  { get; private set; }
    public Guid          CrewId        { get; private set; }
    public Guid          VendorId      { get; private set; }
    public decimal       AgreedAmount  { get; private set; }
    public decimal?      PaidAmount    { get; private set; }
    public PaymentStatus Status        { get; private set; }
    public PaymentMethod? Method       { get; private set; }
    public string?       TransactionRef { get; private set; }
    public string?       Notes          { get; private set; }
    public DateTime?     PaidAt        { get; private set; }
    public Guid?         PayrollBatchId { get; private set; }

    // Navigation
    public Event           Event       { get; private set; } = default!;
    public EventAssignment Assignment  { get; private set; } = default!;
    public User            Crew        { get; private set; } = default!;
    public User            Vendor      { get; private set; } = default!;
    public PayrollBatch?   PayrollBatch { get; private set; }

    public void Approve()
    {
        if (Status != PaymentStatus.Pending && Status != PaymentStatus.OnHold)
            throw new InvalidOperationException("Only Pending/OnHold payments can be approved.");
        Status = PaymentStatus.Approved;
    }

    public void MarkPaid(decimal paidAmount, PaymentMethod method, string? transactionRef)
    {
        if (Status != PaymentStatus.Approved)
            throw new InvalidOperationException("Payment must be Approved before marking Paid.");
        PaidAmount     = paidAmount;
        Method         = method;
        TransactionRef = transactionRef;
        Status         = PaymentStatus.Paid;
        PaidAt         = DateTime.UtcNow;
    }

    public void Reject(string reason)
    {
        Status = PaymentStatus.Rejected;
        Notes  = reason;
    }

    public void PutOnHold(string reason)
    {
        Status = PaymentStatus.OnHold;
        Notes  = reason;
    }

    public void AttachToPayroll(Guid batchId) => PayrollBatchId = batchId;

    public void UpdateAmount(decimal newAmount)
    {
        if (Status != PaymentStatus.Pending)
            throw new InvalidOperationException("Cannot update amount after processing has started.");
        AgreedAmount = newAmount;
    }
}
