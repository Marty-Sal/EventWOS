using Asp.Versioning;
using EventWOS.Api.Authorization;
using EventWOS.Application.VendorAllocations.Commands;
using EventWOS.Application.VendorAllocations.DTOs;
using EventWOS.Application.VendorAllocations.Queries;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Common;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EventWOS.Api.Controllers;

/// <summary>
/// Per-vendor staffing quotas on event shifts. Phase C of Scope-of-Work.
///
/// AUTH PATTERN (memory rule #29): every endpoint uses
/// <c>[Permission("vendor_allocations:*")]</c>. Read endpoints (so the
/// vendor's own portal can list "shifts I'm allocated to" later) use
/// <c>vendor_allocations:read</c> which the seeder grants to Manager
/// and Vendor by default. Write endpoints (create/update/archive) use
/// <c>vendor_allocations:write</c> which by seeder default is
/// Admin + Manager only — Vendors can't grant themselves quota.
///
/// All routes are nested under shift_id where it makes sense, since the
/// allocation is meaningless without its shift context.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}")]
[Authorize]
[Produces("application/json")]
public sealed class VendorAllocationsController : ControllerBase
{
    private readonly IMediator    _mediator;
    private readonly ICurrentUser _currentUser;

    public VendorAllocationsController(IMediator mediator, ICurrentUser currentUser)
    {
        _mediator    = mediator;
        _currentUser = currentUser;
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    [Permission("vendor_allocations:read")]
    [HttpGet("event-shifts/{shiftId:guid}/vendor-allocations")]
    public async Task<IActionResult> ListForShift(Guid shiftId, CancellationToken ct)
    {
        var res = await _mediator.Send(new GetVendorAllocationsForShiftQuery(shiftId), ct);
        return res.IsSuccess
            ? Ok(ApiResponse<IReadOnlyList<VendorShiftAllocationDto>>.Ok(res.Value))
            : BadRequest(ApiResponse<IReadOnlyList<VendorShiftAllocationDto>>.Fail(res.Error.Message));
    }

    // ── Write ─────────────────────────────────────────────────────────────────

    public sealed record CreateAllocationRequest(Guid VendorId, int Quota);
    public sealed record UpdateAllocationRequest(int Quota);

    [Permission("vendor_allocations:write")]
    [HttpPost("event-shifts/{shiftId:guid}/vendor-allocations")]
    public async Task<IActionResult> Create(
        Guid shiftId, [FromBody] CreateAllocationRequest req, CancellationToken ct)
    {
        var res = await _mediator.Send(
            new CreateVendorAllocationCommand(
                shiftId, req.VendorId, req.Quota, _currentUser.UserId!.Value), ct);
        return res.IsSuccess
            ? Ok(ApiResponse<VendorShiftAllocationDto>.Ok(res.Value))
            : BadRequest(ApiResponse<VendorShiftAllocationDto>.Fail(res.Error.Message));
    }

    [Permission("vendor_allocations:write")]
    [HttpPut("vendor-allocations/{id:guid}")]
    public async Task<IActionResult> Update(
        Guid id, [FromBody] UpdateAllocationRequest req, CancellationToken ct)
    {
        var res = await _mediator.Send(
            new UpdateVendorAllocationCommand(id, req.Quota, _currentUser.UserId!.Value), ct);
        return res.IsSuccess
            ? Ok(ApiResponse<VendorShiftAllocationDto>.Ok(res.Value))
            : BadRequest(ApiResponse<VendorShiftAllocationDto>.Fail(res.Error.Message));
    }

    [Permission("vendor_allocations:write")]
    [HttpDelete("vendor-allocations/{id:guid}")]
    public async Task<IActionResult> Archive(Guid id, CancellationToken ct)
    {
        var res = await _mediator.Send(
            new ArchiveVendorAllocationCommand(id, _currentUser.UserId!.Value), ct);
        return res.IsSuccess
            ? Ok(ApiResponse.Ok("Archived."))
            : BadRequest(ApiResponse.Fail(res.Error.Message));
    }
}
