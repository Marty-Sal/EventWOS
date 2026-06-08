namespace EventWOS.Application.Approval.DTOs;

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
    string?  ReferredVendorName);

public sealed record ApprovalQueueDto(
    int VendorCount,
    int CrewCount,
    IReadOnlyList<PendingRegistrationDto> Vendors,
    IReadOnlyList<PendingRegistrationDto> Crew);
