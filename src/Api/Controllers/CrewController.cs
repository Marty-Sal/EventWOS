using EventOpsOracle.Api.Authorization;
using Asp.Versioning;
using EventOpsOracle.Application.Crew.Commands;
using EventOpsOracle.Application.Crew.Queries;
using EventOpsOracle.Application.Vendors.DTOs;
using EventOpsOracle.Domain.Enums;
using EventOpsOracle.Domain.Interfaces;
using EventOpsOracle.Shared.Common;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EventOpsOracle.Api.Controllers;

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
    [Permission("crew:read")]
    [HttpGet]
    public async Task<IActionResult> GetCrew(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20,
        [FromQuery] string? search = null, [FromQuery] Guid? vendorId = null,
        [FromQuery] string? status = null,
        CancellationToken ct = default)
    {
        if (!_currentUser.HasPermission("crew:read")) return Forbid();

        // Vendors can only see their own crew
        var effectiveVendorId = _currentUser.Role == UserRole.Vendor
            ? _currentUser.UserId
            : vendorId;

        var result = await _mediator.Send(new GetCrewQuery(page, pageSize, search, effectiveVendorId, status), ct);
        return Ok(ApiResponse<PagedCrewResult>.Ok(result.Value));
    }

    /// <summary>Get a single crew member's full profile — "View details" modal.</summary>
    [Permission("crew:read")]
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetCrewById(Guid id, CancellationToken ct)
    {
        if (!_currentUser.HasPermission("crew:read")) return Forbid();
        var result = await _mediator.Send(new EventOpsOracle.Application.Crew.Queries.GetCrewByIdQuery(id), ct);
        return result.IsSuccess
            ? Ok(ApiResponse<CrewDetailDto>.Ok(result.Value))
            : NotFound(ApiResponse<CrewDetailDto>.Fail(result.Error.Message));
    }

    /// <summary>Create a crew member. Admin/Vendor.</summary>
    [Permission("crew:write")]
    [HttpPost]
    public async Task<IActionResult> CreateCrew([FromBody] CreateCrewRequest req, CancellationToken ct)
    {
        if (!_currentUser.HasPermission("crew:write")) return Forbid();
        var result = await _mediator.Send(new CreateCrewCommand(req.Mobile, req.FullName, req.Email, req.ReferralCode, _currentUser.UserId!.Value), ct);
        return result.IsSuccess
            ? Created(string.Empty, ApiResponse<CrewDto>.Ok(result.Value))
            : BadRequest(ApiResponse<CrewDto>.Fail(result.Error.Message));
    }

    /// <summary>Crew joins a vendor via referral code.</summary>
    [Permission("crew:write")]
    [HttpPost("join-vendor")]
    public async Task<IActionResult> JoinVendor([FromBody] JoinVendorRequest req, CancellationToken ct)
    {
        if (_currentUser.Role != UserRole.Crew) return Forbid();
        var result = await _mediator.Send(new JoinVendorCommand(_currentUser.UserId!.Value, req.ReferralCode), ct);
        return result.IsSuccess ? Ok(ApiResponse.Ok("Joined vendor successfully.")) : BadRequest(ApiResponse.Fail(result.Error.Message));
    }

    /// <summary>Suspend / reactivate a crew member — mirrors the Vendor status endpoint.</summary>
    [Permission("crew:write")]
    [HttpPatch("{id:guid}/status")]
    public async Task<IActionResult> ChangeStatus(Guid id, [FromBody] ChangeCrewStatusRequest req, CancellationToken ct)
    {
        if (!_currentUser.HasPermission("crew:write")) return Forbid();
        var result = await _mediator.Send(new ChangeCrewStatusCommand(id, req.Status, _currentUser.UserId!.Value, _currentUser.Role == UserRole.Vendor), ct);
        return result.IsSuccess ? Ok(ApiResponse.Ok()) : BadRequest(ApiResponse.Fail(result.Error.Message));
    }
}

public sealed record ChangeCrewStatusRequest(string Status);
