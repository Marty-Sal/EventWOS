using EventOpsOracle.Api.Authorization;
using EventOpsOracle.Domain.Enums;
using Asp.Versioning;
using EventOpsOracle.Application.Vendors.Commands;
using EventOpsOracle.Application.Analytics.Queries;
using EventOpsOracle.Application.Ratings.Queries;
using EventOpsOracle.Application.Vendors.DTOs;
using EventOpsOracle.Application.Vendors.Queries;
using EventOpsOracle.Domain.Interfaces;
using EventOpsOracle.Shared.Common;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EventOpsOracle.Api.Controllers;

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
    [Permission("vendors:read")]
    [HttpGet]
    public async Task<IActionResult> GetVendors(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] string? search = null,
        [FromQuery] string? status = null,
        CancellationToken ct = default)
    {
        // Vendors can only see themselves — return their own record as single-item page
        if (_currentUser.Role == EventOpsOracle.Domain.Enums.UserRole.Vendor)
        {
            var selfResult = await _mediator.Send(new GetVendorByIdQuery(_currentUser.UserId!.Value), ct);
            if (selfResult.IsFailure) return Forbid();
            var v = selfResult.Value;
            var item = new VendorListItemDto(
                v.Id, v.Mobile, v.FullName, v.BusinessName,
                v.Status, v.ReferralCode, v.Rating, v.RatingCount,
                v.EventsCompleted, v.CrewCount, v.CreatedAt);
            var single = new PagedVendorResult(new[] { item }, 1, 1, 1);
            return Ok(ApiResponse<PagedVendorResult>.Ok(single));
        }

        if (!_currentUser.HasPermission("vendors:read")) return Forbid();
        var result = await _mediator.Send(new GetVendorsQuery(page, pageSize, search, status), ct);
        return Ok(ApiResponse<PagedVendorResult>.Ok(result.Value));
    }

    /// <summary>Get vendor by ID.</summary>
    [Permission("vendors:read")]
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
    [Permission("vendors:write")]
    [HttpPost]
    public async Task<IActionResult> CreateVendor([FromBody] CreateVendorRequest req, CancellationToken ct)
    {
        if (!_currentUser.HasPermission("vendors:write")) return Forbid();
        var result = await _mediator.Send(new CreateVendorCommand(req.Mobile, req.FullName, req.BusinessName, req.Email, _currentUser.UserId!.Value), ct);
        return result.IsSuccess
            ? CreatedAtAction(nameof(GetVendor), new { id = result.Value.Id, version = "1" },
                ApiResponse<VendorDto>.Ok(result.Value))
            : BadRequest(ApiResponse<VendorDto>.Fail(result.Error.Message));
    }

    /// <summary>
    /// Completed events this vendor worked, each with the rating already given if
    /// there is one. Feeds the event picker in the rating dialog -- a vendor
    /// rating has to be attached to a specific event, so the rater needs to see
    /// which ones are open to rate.
    /// </summary>
    [Permission("vendors:read")]
    [HttpGet("{id:guid}/rateable-events")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<RateableEventDto>>), 200)]
    public async Task<IActionResult> GetRateableEvents(Guid id, CancellationToken ct)
    {
        if (!_currentUser.HasPermission("vendors:read")) return Forbid();
        var result = await _mediator.Send(new GetRateableEventsQuery(id), ct);
        return result.IsSuccess
            ? Ok(ApiResponse<IReadOnlyList<RateableEventDto>>.Ok(result.Value))
            : BadRequest(ApiResponse<IReadOnlyList<RateableEventDto>>.Fail(result.Error.Message));
    }

    /// <summary>
    /// Admin/Manager rates a vendor's performance and cooperation on ONE completed
    /// event. Event-scoped because a vendor's reputation is the average of the
    /// events they worked -- the previous global PATCH overwrote the single score
    /// each time, so a second event silently erased the first.
    /// </summary>
    [Permission("vendors:write")]
    [HttpPost("{id:guid}/events/{eventId:guid}/rating")]
    public async Task<IActionResult> RateVendor(
        Guid id, Guid eventId, [FromBody] RateVendorRequest req, CancellationToken ct)
    {
        if (!_currentUser.HasPermission("vendors:write")) return Forbid();
        var result = await _mediator.Send(
            new RateVendorCommand(eventId, id, req.Performance, req.Cooperation, req.Comment), ct);
        return result.IsSuccess ? Ok(ApiResponse.Ok("Rating saved."))
                                : BadRequest(ApiResponse.Fail(result.Error.Message));
    }

    /// <summary>Change vendor status. Admin only.</summary>
    [Permission("vendors:write")]
    [HttpPatch("{id:guid}/status")]
    public async Task<IActionResult> ChangeStatus(Guid id, [FromBody] ChangeVendorStatusRequest req, CancellationToken ct)
    {
        if (!_currentUser.HasPermission("vendors:write")) return Forbid();
        var result = await _mediator.Send(new ChangeVendorStatusCommand(id, req.Status, _currentUser.UserId!.Value), ct);
        return result.IsSuccess ? Ok(ApiResponse.Ok()) : BadRequest(ApiResponse.Fail(result.Error.Message));
    }

    // ── Vendor Own Report ─────────────────────────────────────────────────────

    /// <summary>
    /// Vendor views their own analytics report (crew stats, attendance, payments).
    /// Profile:read is sufficient — Vendor always has it.
    /// </summary>
    [Permission("profile:read")]
    [HttpGet("my/report")]
    public async Task<IActionResult> GetMyReport(CancellationToken ct)
    {
        var vendorId = _currentUser.UserId;
        if (vendorId is null) return Unauthorized();
        var result = await _mediator.Send(new GetVendorReportQuery(vendorId.Value), ct);
        return result.IsSuccess
            ? Ok(ApiResponse<VendorReportDto>.Ok(result.Value))
            : NotFound(ApiResponse<VendorReportDto>.Fail(result.Error.Message));
    }
}

public sealed record ChangeVendorStatusRequest(string Status);
