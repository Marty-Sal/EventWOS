using EventWOS.Shared.Result;
using MediatR;

namespace EventWOS.Application.Registration.Commands;

/// <summary>
/// Public self-registration for Crew. Optional ReferralCode binds them
/// to a Vendor (resolved during handling). Pending until approved.
/// </summary>
public sealed record RegisterCrewCommand(
    string Username,
    string Email,
    string Mobile,
    string Password,
    string FullName,
    string? ReferralCode,
    string? City,
    string? Skills,
    int?    ExperienceYears,
    string? Bio
) : IRequest<Result<RegistrationResponse>>;
