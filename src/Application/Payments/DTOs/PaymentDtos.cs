namespace EventWOS.Application.Payments.DTOs;

public sealed record CrewPaymentDto(
    Guid     Id,
    Guid     EventId,
    string   EventTitle,
    Guid     AssignmentId,
    Guid     CrewId,
    string   CrewName,
    string   CrewMobile,
    Guid?    VendorId,
    string?  VendorName,
    decimal  AgreedAmount,
    decimal? PaidAmount,
    string   Status,
    string?  Method,
    string?  TransactionRef,
    string?  Notes,
    DateTime? PaidAt,
    Guid?    PayrollBatchId,
    string   CrewAcknowledgment,    // "None" | "Received" | "Pending"
    DateTime? AcknowledgedAt,
    string?  AcknowledgmentNote,
    string?  BatchStatus,            // "Draft" | "Submitted" | "Approved" | "Disbursed" — null if no batch
    decimal? BatchTotal,             // Parent batch's total — vendor-level amount sent by the organiser
    DateTime CreatedDate,
    // ── Phase D step 28: shift context (nullable for legacy pre-multi-shift rows). ──
    // Lets every Payments view distinguish two payments to the same crew
    // for two different shifts at the same event — see the screenshots in
    // the user feedback. Resolved via the assignment's Shift, not stored
    // directly on CrewPayment.
    string?  ShiftScopeName = null,
    DateTime? ShiftStartAt  = null,
    DateTime? ShiftEndAt    = null
);

public sealed record PayrollBatchDto(
    Guid     Id,
    Guid?    VendorId,
    string?  VendorName,
    Guid     EventId,
    string   EventTitle,
    string   BatchRef,
    string   Status,
    decimal  TotalAmount,
    string?  Notes,
    int      PaymentCount,
    DateTime? SubmittedAt,
    DateTime? ApprovedAt,
    DateTime? DisbursedAt,
    DateTime CreatedDate
);

public sealed record PagedPaymentResult(
    IReadOnlyList<CrewPaymentDto> Items,
    int TotalCount,
    int Page,
    int PageSize
);

public sealed record PagedPayrollResult(
    IReadOnlyList<PayrollBatchDto> Items,
    int TotalCount,
    int Page,
    int PageSize
);
