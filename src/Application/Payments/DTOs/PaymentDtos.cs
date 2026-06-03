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
    DateTime CreatedDate
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
