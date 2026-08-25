using EventOpsOracle.Application.Files.DTOs;

namespace EventOpsOracle.Application.Approval.DTOs;

/// <summary>One row per pending registration.</summary>
public sealed record PendingRegistrationDto(
    Guid    UserId,
    string  Username,
    string  Email,
    string  Mobile,
    string  FullName,
    string  Role,
    DateTime RegisteredAt,
    // Vendor-specific
    string?  BusinessName,
    string?  ContactPersonName,
    string?  City,
    string?  Website,
    // Crew-specific
    string?  Skills,
    int?     ExperienceYears,
    string?  ReferralCodeUsed,
    Guid?    ReferredVendorId,
    string?  ReferredVendorName,
    // Full-detail fields for the "View details" modal — not shown in the
    // summary row, only when the reviewer expands a record. GstNumber and
    // Address are Vendor-only; Bio is used by both ("About your business"
    // for Vendor, general bio for Crew).
    string?  GstNumber,
    string?  Address,
    string?  State,
    string?  Bio,
    DateTime? DateOfBirth,
    // Uploaded documents (profile photo / ID proof for Crew, profile photo
    // for Vendor) so the reviewer can open/download them without leaving
    // the modal.
    IReadOnlyList<FileDocumentDto> Files);

public sealed record ApprovalQueueDto(
    int VendorCount,
    int CrewCount,
    IReadOnlyList<PendingRegistrationDto> Vendors,
    IReadOnlyList<PendingRegistrationDto> Crew);
