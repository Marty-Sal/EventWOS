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
        Guid?  vendorId,           // null for direct-crew payments (no intermediary vendor)
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
    public Guid?         VendorId      { get; private set; }
    public decimal       AgreedAmount  { get; private set; }
    public decimal?      PaidAmount    { get; private set; }
    public PaymentStatus Status        { get; private set; }
    public PaymentMethod? Method       { get; private set; }
    public string?       TransactionRef { get; private set; }
    public string?       Notes          { get; private set; }
    public DateTime?     PaidAt        { get; private set; }
    public Guid?         PayrollBatchId { get; private set; }

    // Crew-side acknowledgement of receipt (step 5 in Payment & Settlement Lifecycle).
    public PaymentAcknowledgment CrewAcknowledgment   { get; private set; } = PaymentAcknowledgment.None;
    public DateTime?             AcknowledgedAt       { get; private set; }
    public string?               AcknowledgmentNote   { get; private set; }

    // Navigation
    public Event           Event       { get; private set; } = default!;
    public EventAssignment Assignment  { get; private set; } = default!;
    public User            Crew        { get; private set; } = default!;
    public User?           Vendor      { get; private set; }
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

    /// <summary>
    /// Vendor sets each crew's individual cut after the manager has paid the
    /// vendor-level total. Only legal on vendor-mediated rows where the
    /// amount was created as 0 (placeholder).
    /// </summary>
    public void SetAgreedAmountByVendor(decimal newAmount)
    {
        if (VendorId is null)
            throw new InvalidOperationException("Direct-crew payment amounts are set by the manager.");
        if (Status != PaymentStatus.Approved)
            throw new InvalidOperationException("Vendor can only adjust amount on Approved rows.");
        if (newAmount <= 0)
            throw new InvalidOperationException("Amount must be greater than zero.");
        AgreedAmount = newAmount;
    }

    /// <summary>
    /// Crew confirms they received the money. Only legal once Vendor has marked
    /// the payment Paid.
    /// </summary>
    public void AcknowledgeReceived(string? note = null)
    {
        if (Status != PaymentStatus.Paid)
            throw new InvalidOperationException("Only a Paid payment can be marked as Received.");
        CrewAcknowledgment = PaymentAcknowledgment.Received;
        AcknowledgedAt     = DateTime.UtcNow;
        AcknowledgmentNote = note;
    }

    /// <summary>
    /// Crew flags the payment as not actually received yet (dispute). Allowed once
    /// either: (a) the payment is Paid (vendor said they paid but crew didn't get it),
    /// or (b) the payment is Approved AND vendor-mediated (so the manager has paid
    /// the vendor but the vendor hasn't paid the crew out yet). The status is left
    /// untouched; only the acknowledgement flag flips so Admin/Manager see the dispute.
    /// </summary>
    public void AcknowledgePending(string? note = null)
    {
        var ok = Status == PaymentStatus.Paid
              || (Status == PaymentStatus.Approved && VendorId is not null);
        if (!ok)
            throw new InvalidOperationException("Payment must be Paid before crew can flag it Pending.");
        CrewAcknowledgment = PaymentAcknowledgment.Pending;
        AcknowledgedAt     = DateTime.UtcNow;
        AcknowledgmentNote = note;
    }
}
