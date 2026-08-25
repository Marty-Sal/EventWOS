using EventOpsOracle.Api.Authorization;
using Asp.Versioning;
using EventOpsOracle.Application.Sessions.Commands;
using EventOpsOracle.Application.Sessions.Queries;
using EventOpsOracle.Application.Users.Commands;
using EventOpsOracle.Application.Ratings.Queries;
using EventOpsOracle.Application.Users.DTOs;
using EventOpsOracle.Application.Users.Queries;
using EventOpsOracle.Domain.Enums;
using EventOpsOracle.Domain.Interfaces;
using EventOpsOracle.Shared.Common;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EventOpsOracle.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/users")]
[Authorize]
[Produces("application/json")]
public sealed class UsersController : ControllerBase
{
    private readonly IMediator    _mediator;
    private readonly ICurrentUser _currentUser;

    public UsersController(IMediator mediator, ICurrentUser currentUser)
    {
        _mediator    = mediator;
        _currentUser = currentUser;
    }

    /// <summary>
    /// A user's rating breakdown: overall average, each axis separately, the
    /// star distribution, and recent feedback.
    ///
    /// Anyone may read their OWN summary -- crew and vendors need to see how they
    /// are being scored. Reading someone else's needs users:read, since ratings
    /// carry the rater's name and free-text comments about a named person.
    /// </summary>
    [Permission("profile:read")]
    [HttpGet("{id:guid}/rating-summary")]
    [ProducesResponseType(typeof(ApiResponse<UserRatingSummaryDto>), 200)]
    public async Task<IActionResult> GetRatingSummary(
        Guid id, [FromQuery] int recent = 10, CancellationToken ct = default)
    {
        if (id != _currentUser.UserId && !_currentUser.HasPermission("users:read"))
            return Forbid();

        var result = await _mediator.Send(new GetUserRatingSummaryQuery(id, recent), ct);
        return result.IsSuccess
            ? Ok(ApiResponse<UserRatingSummaryDto>.Ok(result.Value))
            : NotFound(ApiResponse<UserRatingSummaryDto>.Fail(result.Error.Message));
    }

    /// <summary>
    /// Get authenticated user's own profile.
    /// Requires profile:read — every role has this by default.
    /// </summary>
    [Permission("profile:read")]
    [HttpGet("me")]
    [ProducesResponseType(typeof(ApiResponse<UserProfileDto>), 200)]
    public async Task<IActionResult> GetMe(CancellationToken ct)
    {
        var result = await _mediator.Send(new GetCurrentUserQuery(_currentUser.UserId!.Value), ct);
        return result.IsSuccess
            ? Ok(ApiResponse<UserProfileDto>.Ok(result.Value))
            : NotFound(ApiResponse<UserProfileDto>.Fail(result.Error.Message));
    }

    /// <summary>
    /// Update authenticated user's own profile (name, email, avatar).
    /// Requires profile:write — every role has this by default.
    /// </summary>
    [Permission("profile:write")]
    [HttpPut("me")]
    [ProducesResponseType(typeof(ApiResponse), 200)]
    public async Task<IActionResult> UpdateMe([FromBody] UpdateProfileRequest dto, CancellationToken ct)
    {
        var command = new UpdateProfileCommand(
            _currentUser.UserId!.Value, dto.FullName, dto.Email, dto.AvatarUrl, dto.InviteMessageTemplate,
            dto.DateOfBirth, dto.City, dto.State, dto.Address, dto.Bio, dto.Skills, dto.ExperienceYears,
            dto.BusinessName, dto.ContactPersonName, dto.GstNumber, dto.Website);
        var result = await _mediator.Send(command, ct);
        return result.IsSuccess
            ? Ok(ApiResponse.Ok("Profile updated."))
            : BadRequest(ApiResponse.Fail(result.Error.Message));
    }

    /// <summary>List all users. Requires users:read (Admin / Manager only).</summary>
    [Permission("users:read")]
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<UserDto>>), 200)]
    public async Task<IActionResult> GetUsers(
        [FromQuery] int page     = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string?     search = null,
        [FromQuery] UserRole?   role   = null,
        [FromQuery] UserStatus? status = null,
        CancellationToken ct           = default)
    {
        var result = await _mediator.Send(new GetUsersQuery(page, pageSize, search, role, status), ct);
        return Ok(ApiResponse<PagedResult<UserDto>>.Ok(result.Value));
    }

    /// <summary>Change user status (suspend/activate/deactivate). Admin only.</summary>
    [Permission("users:status")]
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
        var result  = await _mediator.Send(command, ct);
        return result.IsSuccess
            ? Ok(ApiResponse.Ok())
            : BadRequest(ApiResponse.Fail(result.Error.Message));
    }
}
