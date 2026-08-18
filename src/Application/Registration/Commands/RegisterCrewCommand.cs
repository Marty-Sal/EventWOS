using EventWOS.Application.Files;
using EventWOS.Shared.Result;
using MediatR;

namespace EventWOS.Application.Registration.Commands;

/// <summary>
/// Public self-registration for Crew. Optional ReferralCode binds them
/// to a Vendor (resolved during handling). Pending until approved.
///
/// IdentificationProof is mandatory (a verification document is required
/// before a crew member can be approved); ProfilePhoto is optional. Both
/// are stored via IFileUploadStorer using the crew record's own new Id as
/// OwnerId — see RegisterCrewHandler for why that's safe to do before the
/// User row is even saved.
/// </summary>
public sealed record RegisterCrewCommand(
    string Username,
    string Email,
    string Mobile,
    string Password,
    string FullName,
    DateTime DateOfBirth,
    string? ReferralCode,
    string? City,
    string? Skills,
    int?    ExperienceYears,
    string? Bio,
    FileUploadPayload IdentificationProof,
    FileUploadPayload? ProfilePhoto
) : IRequest<Result<RegistrationResponse>>;
