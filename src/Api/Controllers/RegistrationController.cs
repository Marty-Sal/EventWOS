using Asp.Versioning;
using EventWOS.Application.Registration.Commands;
using EventWOS.Shared.Common;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EventWOS.Api.Controllers;

/// <summary>
/// Public self-registration endpoints for Vendors and Crew. Both endpoints
/// create accounts in PendingApproval status — login is blocked until an
/// Admin/Manager approves via the approval queue. See AdminController
/// for the approve/reject endpoints (Phase 4).
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/auth/register")]
[Produces("application/json")]
public sealed class RegistrationController : ControllerBase
{
    private readonly IMediator _mediator;
    public RegistrationController(IMediator mediator) => _mediator = mediator;

    [HttpPost("vendor")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<RegistrationResponse>), 200)]
    [ProducesResponseType(typeof(ApiResponse), 400)]
    [ProducesResponseType(typeof(ApiResponse), 409)]
    public async Task<IActionResult> RegisterVendor(
        [FromBody] RegisterVendorDto dto, CancellationToken ct)
    {
        var cmd = new RegisterVendorCommand(
            dto.Username, dto.Email, dto.Mobile, dto.Password, dto.FullName,
            dto.BusinessName, dto.ContactPersonName, dto.GstNumber,
            dto.Address, dto.City, dto.State, dto.Website, dto.Bio);

        var result = await _mediator.Send(cmd, ct);
        if (result.IsFailure)
        {
            var status = result.Error.Code switch
            {
                "Registration.UsernameTaken" => 409,
                "Registration.MobileTaken"   => 409,
                "Registration.EmailTaken"    => 409,
                "Registration.CoolDown"      => 429,
                _ => 400
            };
            return StatusCode(status, ApiResponse<RegistrationResponse>.Fail(result.Error.Message));
        }
        return Ok(ApiResponse<RegistrationResponse>.Ok(result.Value));
    }

    [HttpPost("crew")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<RegistrationResponse>), 200)]
    [ProducesResponseType(typeof(ApiResponse), 400)]
    [ProducesResponseType(typeof(ApiResponse), 409)]
    public async Task<IActionResult> RegisterCrew(
        [FromBody] RegisterCrewDto dto, CancellationToken ct)
    {
        var cmd = new RegisterCrewCommand(
            dto.Username, dto.Email, dto.Mobile, dto.Password, dto.FullName,
            dto.ReferralCode, dto.City, dto.Skills, dto.ExperienceYears, dto.Bio);

        var result = await _mediator.Send(cmd, ct);
        if (result.IsFailure)
        {
            var status = result.Error.Code switch
            {
                "Registration.UsernameTaken"   => 409,
                "Registration.MobileTaken"     => 409,
                "Registration.EmailTaken"      => 409,
                "Registration.CoolDown"        => 429,
                "Registration.InvalidReferral" => 400,
                _ => 400
            };
            return StatusCode(status, ApiResponse<RegistrationResponse>.Fail(result.Error.Message));
        }
        return Ok(ApiResponse<RegistrationResponse>.Ok(result.Value));
    }
}

// ─── DTOs ──────────────────────────────────────────────────────────────────
public sealed record RegisterVendorDto(
    string Username, string Email, string Mobile, string Password, string FullName,
    string BusinessName, string? ContactPersonName, string? GstNumber,
    string? Address, string? City, string? State, string? Website, string? Bio);

public sealed record RegisterCrewDto(
    string Username, string Email, string Mobile, string Password, string FullName,
    string? ReferralCode, string? City, string? Skills, int? ExperienceYears, string? Bio);
