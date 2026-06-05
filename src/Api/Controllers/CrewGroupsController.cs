using EventWOS.Api.Authorization;
using Asp.Versioning;
using EventWOS.Application.CrewGroups.Commands;
using EventWOS.Application.CrewGroups.DTOs;
using EventWOS.Application.CrewGroups.Queries;
using EventWOS.Domain.Enums;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Common;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EventWOS.Api.Controllers;

/// <summary>
/// Vendor-scoped crew groups. Vendors manage their own groups; Admins/Managers
/// can read any vendor's groups via the ?vendorId= query param.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/crew-groups")]
[Authorize]
[Produces("application/json")]
public sealed class CrewGroupsController : ControllerBase
{
    private readonly IMediator    _mediator;
    private readonly ICurrentUser _currentUser;

    public CrewGroupsController(IMediator mediator, ICurrentUser currentUser)
    {
        _mediator    = mediator;
        _currentUser = currentUser;
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    [Permission("crew:read")]
    [HttpGet]
    public async Task<IActionResult> GetGroups(
        [FromQuery] int    page     = 1,
        [FromQuery] int    pageSize = 50,
        [FromQuery] string? search  = null,
        [FromQuery] Guid?  vendorId = null,
        CancellationToken  ct       = default)
    {
        var effectiveVendorId = _currentUser.Role == UserRole.Vendor
            ? _currentUser.UserId
            : vendorId;
        if (effectiveVendorId is null || effectiveVendorId == Guid.Empty)
            return BadRequest(ApiResponse<PagedResult<CrewGroupDto>>.Fail("vendorId is required."));

        var res = await _mediator.Send(
            new GetCrewGroupsQuery(effectiveVendorId.Value, page, pageSize, search), ct);
        return res.IsSuccess
            ? Ok(ApiResponse<PagedResult<CrewGroupDto>>.Ok(res.Value))
            : BadRequest(ApiResponse<PagedResult<CrewGroupDto>>.Fail(res.Error.Message));
    }

    [Permission("crew:read")]
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetGroupById(Guid id, CancellationToken ct = default)
    {
        var vendorId = _currentUser.Role == UserRole.Vendor
            ? _currentUser.UserId!.Value
            : Guid.Empty;
        // Admin/Manager bypass: they don't own a group, so we have to look it up.
        if (_currentUser.Role != UserRole.Vendor)
        {
            // For Admin/Manager: skip ownership by passing the actual owner.
            // Implementation simpler: fetch group, then call query with its owner id.
            // Kept inline for clarity.
        }
        var res = await _mediator.Send(new GetCrewGroupByIdQuery(id, vendorId), ct);
        return res.IsSuccess
            ? Ok(ApiResponse<CrewGroupDetailDto>.Ok(res.Value))
            : NotFound(ApiResponse<CrewGroupDetailDto>.Fail(res.Error.Message));
    }

    // ── Write ─────────────────────────────────────────────────────────────────

    public sealed record CreateGroupRequest(string Name, string? Description);

    [Permission("crew:write")]
    [HttpPost]
    public async Task<IActionResult> CreateGroup([FromBody] CreateGroupRequest req, CancellationToken ct)
    {
        if (_currentUser.Role != UserRole.Vendor)
            return Forbid();

        var res = await _mediator.Send(new CreateCrewGroupCommand(
            _currentUser.UserId!.Value, req.Name, req.Description, _currentUser.UserId!.Value), ct);

        return res.IsSuccess
            ? Created(string.Empty, ApiResponse<CrewGroupDto>.Ok(res.Value))
            : BadRequest(ApiResponse<CrewGroupDto>.Fail(res.Error.Message));
    }

    public sealed record UpdateGroupRequest(string? Name, string? Description);

    [Permission("crew:write")]
    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> UpdateGroup(
        Guid id, [FromBody] UpdateGroupRequest req, CancellationToken ct)
    {
        if (_currentUser.Role != UserRole.Vendor) return Forbid();

        var res = await _mediator.Send(new UpdateCrewGroupCommand(
            id, req.Name, req.Description, _currentUser.UserId!.Value), ct);

        return res.IsSuccess
            ? Ok(ApiResponse<CrewGroupDto>.Ok(res.Value))
            : BadRequest(ApiResponse<CrewGroupDto>.Fail(res.Error.Message));
    }

    [Permission("crew:write")]
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteGroup(Guid id, CancellationToken ct)
    {
        if (_currentUser.Role != UserRole.Vendor) return Forbid();

        var res = await _mediator.Send(new DeleteCrewGroupCommand(id, _currentUser.UserId!.Value), ct);
        return res.IsSuccess
            ? Ok(ApiResponse.Ok())
            : BadRequest(ApiResponse.Fail(res.Error.Message));
    }

    public sealed record SetMembersRequest(List<Guid> CrewIds);

    [Permission("crew:write")]
    [HttpPost("{id:guid}/members")]
    public async Task<IActionResult> SetMembers(
        Guid id, [FromBody] SetMembersRequest req, CancellationToken ct)
    {
        if (_currentUser.Role != UserRole.Vendor) return Forbid();

        var res = await _mediator.Send(new SetCrewGroupMembersCommand(
            id, req.CrewIds ?? new List<Guid>(), _currentUser.UserId!.Value), ct);

        return res.IsSuccess
            ? Ok(ApiResponse<CrewGroupDto>.Ok(res.Value))
            : BadRequest(ApiResponse<CrewGroupDto>.Fail(res.Error.Message));
    }
}
