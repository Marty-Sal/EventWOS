using Asp.Versioning;
using EventWOS.Application.Crew.Commands;
using EventWOS.Application.Crew.Queries;
using EventWOS.Application.Vendors.DTOs;
using EventWOS.Domain.Enums;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Common;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EventWOS.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/crew")]
[Authorize]
[Produces("application/json")]
public sealed class CrewController : ControllerBase
{
    private readonly IMediator    _mediator;
    private readonly ICurrentUser _currentUser;

    public CrewController(IMediator mediator, ICurrentUser currentUser)
    {
        _mediator    = mediator;
        _currentUser = currentUser;
    }

    /// <summary>List crew. Admin/Manager sees all; Vendor sees own crew.</summary>
    [HttpGet]
    public async Task<IActionResult> GetCrew(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20,
        [FromQuery] string? search = null, [FromQuery] Guid? vendorId = null,
        CancellationToken ct = default)
    {
        if (!_currentUser.HasPermission("crew:read")) return Forbid();

        // Vendors can only see their own crew
        var effectiveVendorId = _currentUser.Role == UserRole.Vendor
            ? _currentUser.UserId
            : vendorId;

        var result = await _mediator.Send(new GetCrewQuery(page, pageSize, search, effectiveVendorId), ct);
        return Ok(ApiResponse<PagedCrewResult>.Ok(result.Value));
    }

    /// <summary>Create a crew member. Admin/Vendor.</summary>
    [HttpPost]
    public async Task<IActionResult> CreateCrew([FromBody] CreateCrewRequest req, CancellationToken ct)
    {
        if (!_currentUser.HasPermission("crew:write")) return Forbid();
        var result = await _mediator.Send(new CreateCrewCommand(req.Mobile, req.FullName, req.Email, req.ReferralCode), ct);
        return result.IsSuccess
            ? Created(string.Empty, ApiResponse<CrewDto>.Ok(result.Value))
            : BadRequest(ApiResponse<CrewDto>.Fail(result.Error.Message));
    }

    /// <summary>Crew joins a vendor via referral code.</summary>
    [HttpPost("join-vendor")]
    public async Task<IActionResult> JoinVendor([FromBody] JoinVendorRequest req, CancellationToken ct)
    {
        if (_currentUser.Role != UserRole.Crew) return Forbid();
        var result = await _mediator.Send(new JoinVendorCommand(_currentUser.UserId!.Value, req.ReferralCode), ct);
        return result.IsSuccess ? Ok(ApiResponse.Ok("Joined vendor successfully.")) : BadRequest(ApiResponse.Fail(result.Error.Message));
    }
}
