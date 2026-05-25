using EventWOS.Domain.Enums;
using Asp.Versioning;
using EventWOS.Application.Vendors.Commands;
using EventWOS.Application.Vendors.DTOs;
using EventWOS.Application.Vendors.Queries;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Common;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EventWOS.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/vendors")]
[Authorize]
[Produces("application/json")]
public sealed class VendorsController : ControllerBase
{
    private readonly IMediator     _mediator;
    private readonly ICurrentUser  _currentUser;

    public VendorsController(IMediator mediator, ICurrentUser currentUser)
    {
        _mediator    = mediator;
        _currentUser = currentUser;
    }

    /// <summary>
    /// List vendors.
    /// Admin/Manager: full paginated list.
    /// Vendor: returns only their own record as a single-item list.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetVendors(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] string? search = null,
        CancellationToken ct = default)
    {
        // Vendors can only see themselves — return their own record as single-item page
        if (_currentUser.Role == EventWOS.Domain.Enums.UserRole.Vendor)
        {
            var selfResult = await _mediator.Send(new GetVendorByIdQuery(_currentUser.UserId!.Value), ct);
            if (selfResult.IsFailure) return Forbid();
            var v = selfResult.Value;
            var item = new VendorListItemDto(
                v.Id, v.Mobile, v.FullName, v.BusinessName,
                v.Status, v.ReferralCode, v.Rating, v.EventsCompleted, v.CrewCount, v.CreatedAt);
            var single = new PagedVendorResult(new[] { item }, 1, 1, 1);
            return Ok(ApiResponse<PagedVendorResult>.Ok(single));
        }

        if (!_currentUser.HasPermission("vendors:read")) return Forbid();
        var result = await _mediator.Send(new GetVendorsQuery(page, pageSize, search), ct);
        return Ok(ApiResponse<PagedVendorResult>.Ok(result.Value));
    }

    /// <summary>Get vendor by ID.</summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetVendor(Guid id, CancellationToken ct)
    {
        if (!_currentUser.HasPermission("vendors:read")) return Forbid();
        var result = await _mediator.Send(new GetVendorByIdQuery(id), ct);
        return result.IsSuccess
            ? Ok(ApiResponse<VendorDto>.Ok(result.Value))
            : NotFound(ApiResponse<VendorDto>.Fail(result.Error.Message));
    }

    /// <summary>Create a new vendor. Admin only.</summary>
    [HttpPost]
    public async Task<IActionResult> CreateVendor([FromBody] CreateVendorRequest req, CancellationToken ct)
    {
        if (!_currentUser.HasPermission("vendors:write")) return Forbid();
        var result = await _mediator.Send(new CreateVendorCommand(req.Mobile, req.FullName, req.BusinessName, req.Email), ct);
        return result.IsSuccess
            ? CreatedAtAction(nameof(GetVendor), new { id = result.Value.Id, version = "1" },
                ApiResponse<VendorDto>.Ok(result.Value))
            : BadRequest(ApiResponse<VendorDto>.Fail(result.Error.Message));
    }

    /// <summary>Rate a vendor (0.0–5.0). Admin only.</summary>
    [HttpPatch("{id:guid}/rating")]
    public async Task<IActionResult> RateVendor(Guid id, [FromBody] RateVendorRequest req, CancellationToken ct)
    {
        if (!_currentUser.HasPermission("vendors:write")) return Forbid();
        var result = await _mediator.Send(new RateVendorCommand(id, req.Rating), ct);
        return result.IsSuccess ? Ok(ApiResponse.Ok("Rating updated.")) : BadRequest(ApiResponse.Fail(result.Error.Message));
    }

    /// <summary>Change vendor status. Admin only.</summary>
    [HttpPatch("{id:guid}/status")]
    public async Task<IActionResult> ChangeStatus(Guid id, [FromBody] ChangeVendorStatusRequest req, CancellationToken ct)
    {
        if (!_currentUser.HasPermission("vendors:write")) return Forbid();
        var result = await _mediator.Send(new ChangeVendorStatusCommand(id, req.Status, _currentUser.UserId!.Value), ct);
        return result.IsSuccess ? Ok(ApiResponse.Ok()) : BadRequest(ApiResponse.Fail(result.Error.Message));
    }
}

public sealed record ChangeVendorStatusRequest(string Status);
