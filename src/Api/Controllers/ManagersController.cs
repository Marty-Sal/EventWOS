using Asp.Versioning;
using EventWOS.Application.Users.Commands;
using EventWOS.Application.Users.DTOs;
using EventWOS.Application.Users.Queries;
using EventWOS.Domain.Enums;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Common;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EventWOS.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/managers")]
[Authorize]
[Produces("application/json")]
public sealed class ManagersController : ControllerBase
{
    private readonly IMediator    _mediator;
    private readonly ICurrentUser _currentUser;

    public ManagersController(IMediator mediator, ICurrentUser currentUser)
    {
        _mediator    = mediator;
        _currentUser = currentUser;
    }

    /// <summary>List all managers. Admin only.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<ManagerDto>>), 200)]
    public async Task<IActionResult> GetManagers(
        [FromQuery] int page         = 1,
        [FromQuery] int pageSize     = 20,
        [FromQuery] string? search   = null,
        [FromQuery] UserStatus? status = null,
        CancellationToken ct = default)
    {
        if (!_currentUser.IsInRole(UserRole.Admin)) return Forbid();
        var result = await _mediator.Send(new GetManagersQuery(page, pageSize, search, status), ct);
        return Ok(ApiResponse<PagedResult<ManagerDto>>.Ok(result.Value));
    }

    /// <summary>Create a new Manager account. Admin only.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(ApiResponse<ManagerDto>), 201)]
    public async Task<IActionResult> CreateManager(
        [FromBody] CreateManagerRequest dto, CancellationToken ct)
    {
        if (!_currentUser.IsInRole(UserRole.Admin)) return Forbid();
        var result = await _mediator.Send(
            new CreateManagerCommand(dto.Mobile, dto.FullName, dto.Email, _currentUser.UserId!.Value), ct);
        return result.IsSuccess
            ? CreatedAtAction(nameof(GetManagers), new { }, ApiResponse<ManagerDto>.Ok(result.Value))
            : BadRequest(ApiResponse<ManagerDto>.Fail(result.Error.Message));
    }

    /// <summary>Get all available system permissions. Admin only.</summary>
    [HttpGet("permissions/all")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<PermissionDto>>), 200)]
    public async Task<IActionResult> GetAllPermissions(CancellationToken ct)
    {
        if (!_currentUser.IsInRole(UserRole.Admin)) return Forbid();
        var result = await _mediator.Send(new GetAllPermissionsQuery(), ct);
        return Ok(ApiResponse<IReadOnlyList<PermissionDto>>.Ok(result.Value));
    }

    /// <summary>Grant a permission to a manager. Admin only.</summary>
    [HttpPost("{managerId:guid}/permissions")]
    [ProducesResponseType(typeof(ApiResponse<ManagerPermissionDto>), 201)]
    public async Task<IActionResult> GrantPermission(
        Guid managerId, [FromBody] GrantPermissionRequest dto, CancellationToken ct)
    {
        if (!_currentUser.IsInRole(UserRole.Admin)) return Forbid();
        var result = await _mediator.Send(
            new GrantManagerPermissionCommand(
                managerId, dto.PermissionId, _currentUser.UserId!.Value, dto.ExpiresAt), ct);
        return result.IsSuccess
            ? CreatedAtAction(nameof(GetManagers), new { },
                ApiResponse<ManagerPermissionDto>.Ok(result.Value))
            : BadRequest(ApiResponse<ManagerPermissionDto>.Fail(result.Error.Message));
    }

    /// <summary>Revoke a permission grant from a manager. Admin only.</summary>
    [HttpDelete("{managerId:guid}/permissions/{grantId:guid}")]
    [ProducesResponseType(typeof(ApiResponse), 200)]
    public async Task<IActionResult> RevokePermission(
        Guid managerId, Guid grantId, CancellationToken ct)
    {
        if (!_currentUser.IsInRole(UserRole.Admin)) return Forbid();
        var result = await _mediator.Send(
            new RevokeManagerPermissionCommand(managerId, grantId), ct);
        return result.IsSuccess
            ? Ok(ApiResponse.Ok("Permission revoked."))
            : BadRequest(ApiResponse.Fail(result.Error.Message));
    }

    /// <summary>Suspend or reactivate a manager. Admin only.</summary>
    [HttpPatch("{managerId:guid}/status")]
    [ProducesResponseType(typeof(ApiResponse), 200)]
    public async Task<IActionResult> ChangeStatus(
        Guid managerId, [FromBody] UpdateUserStatusRequest dto, CancellationToken ct)
    {
        if (!_currentUser.IsInRole(UserRole.Admin)) return Forbid();
        var result = await _mediator.Send(
            new ChangeUserStatusCommand(managerId, dto.Status, _currentUser.UserId!.Value), ct);
        return result.IsSuccess
            ? Ok(ApiResponse.Ok())
            : BadRequest(ApiResponse.Fail(result.Error.Message));
    }
}
