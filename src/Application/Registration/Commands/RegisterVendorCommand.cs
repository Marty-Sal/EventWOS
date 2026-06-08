using EventWOS.Shared.Result;
using MediatR;

namespace EventWOS.Application.Registration.Commands;

/// <summary>
/// Public self-registration for a Vendor. Creates a User in PendingApproval
/// status with the VendorProfile fields filled in. Cannot log in until
/// approved by an Admin/Manager.
/// </summary>
public sealed record RegisterVendorCommand(
    string Username,
    string Email,
    string Mobile,
    string Password,
    string FullName,
    string BusinessName,
    string? ContactPersonName,
    string? GstNumber,
    string? Address,
    string? City,
    string? State,
    string? Website,
    string? Bio
) : IRequest<Result<RegistrationResponse>>;

public sealed record RegistrationResponse(
    Guid    UserId,
    string  Status,
    string  Message
);
