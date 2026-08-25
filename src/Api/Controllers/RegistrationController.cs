using Asp.Versioning;
using EventOpsOracle.Application.Files;
using EventOpsOracle.Application.Registration.Commands;
using EventOpsOracle.Application.Registration.Queries;
using EventOpsOracle.Shared.Common;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EventOpsOracle.Api.Controllers;

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
    private const long MaxUploadBytes = 6 * 1024 * 1024; // per-file cap is 5MB; a little headroom for multipart overhead

    public RegistrationController(IMediator mediator) => _mediator = mediator;

    /// <summary>
    /// multipart/form-data — carries the scalar fields plus an optional
    /// ProfilePhoto file. Switched from JSON since the photo field was added;
    /// mirrors the Crew endpoint's shape.
    /// </summary>
    [HttpPost("vendor")]
    [AllowAnonymous]
    [RequestSizeLimit(MaxUploadBytes)]
    [ProducesResponseType(typeof(ApiResponse<RegistrationResponse>), 200)]
    [ProducesResponseType(typeof(ApiResponse), 400)]
    [ProducesResponseType(typeof(ApiResponse), 409)]
    public async Task<IActionResult> RegisterVendor(
        [FromForm] RegisterVendorForm form, CancellationToken ct)
    {
        if (form.ProfilePhoto is not null && form.ProfilePhoto.Length > MaxUploadBytes)
            return BadRequest(ApiResponse.Fail("Profile photo must not exceed 5 MB."));

        var photo = form.ProfilePhoto is { Length: > 0 } ? await ReadUploadAsync(form.ProfilePhoto, ct) : null;

        var cmd = new RegisterVendorCommand(
            form.Username, form.Email, form.Mobile, form.Password, form.FullName,
            form.BusinessName, form.ContactPersonName, form.GstNumber,
            form.Address, form.City, form.State, form.Website, form.Bio, photo,
            form.TermsAccepted, form.TermsVersion);

        var result = await _mediator.Send(cmd, ct);
        if (result.IsFailure)
        {
            var status = result.Error.Code switch
            {
                "Registration.UsernameTaken" => 409,
                "Registration.MobileTaken"   => 409,
                "Registration.EmailTaken"    => 409,
                "Registration.CoolDown"      => 429,
                "Registration.TermsRequired"  => 400,
                "Files.InvalidFile"          => 400,
                "Files.StorageError"         => 500,
                _ => 400
            };
            return StatusCode(status, ApiResponse<RegistrationResponse>.Fail(result.Error.Message));
        }
        return Ok(ApiResponse<RegistrationResponse>.Ok(result.Value));
    }

    /// <summary>
    /// multipart/form-data — carries the scalar fields PLUS the mandatory
    /// IdentificationProof file and optional ProfilePhoto file, since Crew
    /// self-registration happens before any authenticated session/OwnerId
    /// exists (can't go through the [Authorize]-gated FilesController).
    /// </summary>
    [HttpPost("crew")]
    [AllowAnonymous]
    [RequestSizeLimit(2 * MaxUploadBytes)]
    [ProducesResponseType(typeof(ApiResponse<RegistrationResponse>), 200)]
    [ProducesResponseType(typeof(ApiResponse), 400)]
    [ProducesResponseType(typeof(ApiResponse), 409)]
    public async Task<IActionResult> RegisterCrew(
        [FromForm] RegisterCrewForm form, CancellationToken ct)
    {
        if (form.IdentificationProof is null || form.IdentificationProof.Length == 0)
            return BadRequest(ApiResponse.Fail("Identification proof (Aadhaar card, driving licence, or voter ID) is required."));
        if (form.IdentificationProof.Length > MaxUploadBytes)
            return BadRequest(ApiResponse.Fail("Identification proof must not exceed 5 MB."));
        if (form.ProfilePhoto is not null && form.ProfilePhoto.Length > MaxUploadBytes)
            return BadRequest(ApiResponse.Fail("Profile photo must not exceed 5 MB."));

        var idProof = await ReadUploadAsync(form.IdentificationProof, ct);
        var photo   = form.ProfilePhoto is { Length: > 0 } ? await ReadUploadAsync(form.ProfilePhoto, ct) : null;

        var cmd = new RegisterCrewCommand(
            form.Username, form.Email, form.Mobile, form.Password, form.FullName, form.DateOfBirth,
            form.ReferralCode, form.City, form.Skills, form.ExperienceYears, form.Bio,
            idProof, photo, form.TermsAccepted, form.TermsVersion);

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
                "Registration.TermsRequired"    => 400,
                "Files.InvalidFile"            => 400,
                "Files.StorageError"           => 500,
                _ => 400
            };
            return StatusCode(status, ApiResponse<RegistrationResponse>.Fail(result.Error.Message));
        }
        return Ok(ApiResponse<RegistrationResponse>.Ok(result.Value));
    }

    /// <summary>
    /// Live "is this vendor referral code valid" check, so the Crew sign-up
    /// form can validate it inline before the user submits the whole form.
    /// </summary>
    [HttpGet("check-referral")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<ReferralCodeCheckResult>), 200)]
    public async Task<IActionResult> CheckReferralCode([FromQuery] string? code, CancellationToken ct)
    {
        var result = await _mediator.Send(new ValidateReferralCodeQuery(code), ct);
        return Ok(ApiResponse<ReferralCodeCheckResult>.Ok(result));
    }

    private static async Task<FileUploadPayload> ReadUploadAsync(IFormFile file, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        return new FileUploadPayload(ms.ToArray(), file.FileName, file.ContentType);
    }
}

// ─── DTOs / form-binding models ────────────────────────────────────────────
/// <summary>A plain class (not a record) — ASP.NET Core's form binder needs settable properties, especially alongside IFormFile.</summary>
public sealed class RegisterVendorForm
{
    public string Username { get; set; } = default!;
    public string Email { get; set; } = default!;
    public string Mobile { get; set; } = default!;
    public string Password { get; set; } = default!;
    public string FullName { get; set; } = default!;
    public string BusinessName { get; set; } = default!;
    public string? ContactPersonName { get; set; }
    public string? GstNumber { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? Website { get; set; }
    public string? Bio { get; set; }
    public IFormFile? ProfilePhoto { get; set; }
    public bool TermsAccepted { get; set; }
    public int  TermsVersion { get; set; }
}

/// <summary>A plain class (not a record) — ASP.NET Core's form binder needs settable properties, especially alongside IFormFile.</summary>
public sealed class RegisterCrewForm
{
    public string Username { get; set; } = default!;
    public string Email { get; set; } = default!;
    public string Mobile { get; set; } = default!;
    public string Password { get; set; } = default!;
    public string FullName { get; set; } = default!;
    public DateTime DateOfBirth { get; set; }
    public string? ReferralCode { get; set; }
    public string? City { get; set; }
    public string? Skills { get; set; }
    public int? ExperienceYears { get; set; }
    public string? Bio { get; set; }
    public IFormFile? IdentificationProof { get; set; }
    public IFormFile? ProfilePhoto { get; set; }
    public bool TermsAccepted { get; set; }
    public int  TermsVersion { get; set; }
}
