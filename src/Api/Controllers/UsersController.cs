using Asp.Versioning;
using EventWOS.Application.Sessions.Commands;
using EventWOS.Application.Sessions.Queries;
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
[Route("api/v{version:apiVersion}/users")]
[Authorize]
[Produces("application/json")]
public sealed class UsersController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ICurrentUser _currentUser;

    public UsersController(IMediator mediator, ICurrentUser currentUser)
    {
        _mediator = mediator;
        _currentUser = currentUser;
    }

    /// <summary>Get authenticated user's own profile.</summary>
    [HttpGet("me")]
    [ProducesResponseType(typeof(ApiResponse<UserProfileDto>), 200)]
    public async Task<IActionResult> GetMe(CancellationToken ct)
    {
        var result = await _mediator.Send(new GetCurrentUserQuery(_currentUser.UserId!.Value), ct);
        return result.IsSuccess
            ? Ok(ApiResponse<UserProfileDto>.Ok(result.Value))
            : NotFound(ApiResponse<UserProfileDto>.Fail(result.Error.Message));
    }

    /// <summary>Update authenticated user's own profile.</summary>
    [HttpPut("me")]
    [ProducesResponseType(typeof(ApiResponse), 200)]
    public async Task<IActionResult> UpdateMe([FromBody] UpdateProfileRequest dto, CancellationToken ct)
    {
        var command = new UpdateProfileCommand(_currentUser.UserId!.Value, dto.FullName, dto.Email, dto.AvatarUrl);
        var result = await _mediator.Send(command, ct);
        return result.IsSuccess ? Ok(ApiResponse.Ok("Profile updated.")) : BadRequest(ApiResponse.Fail(result.Error.Message));
    }

    /// <summary>List all users. Admin/Manager only.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<UserDto>>), 200)]
    public async Task<IActionResult> GetUsers(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? search = null,
        [FromQuery] UserRole? role = null,
        [FromQuery] UserStatus? status = null,
        CancellationToken ct = default)
    {
        if (!_currentUser.HasPermission("users:read"))
            return Forbid();

        var result = await _mediator.Send(new GetUsersQuery(page, pageSize, search, role, status), ct);
        return Ok(ApiResponse<PagedResult<UserDto>>.Ok(result.Value));
    }

    /// <summary>Change user status (suspend/activate/deactivate). Admin only.</summary>
    [HttpPatch("{id:guid}/status")]
    [ProducesResponseType(typeof(ApiResponse), 200)]
    public async Task<IActionResult> ChangeStatus(
        Guid id,
        [FromBody] UpdateUserStatusRequest dto,
        CancellationToken ct)
    {
        if (!_currentUser.IsInRole(UserRole.Admin))
            return Forbid();

        var command = new ChangeUserStatusCommand(id, dto.Status, _currentUser.UserId!.Value);
        var result = await _mediator.Send(command, ct);
        return result.IsSuccess ? Ok(ApiResponse.Ok()) : BadRequest(ApiResponse.Fail(result.Error.Message));
    }
}
