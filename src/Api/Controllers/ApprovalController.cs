using Asp.Versioning;
using EventWOS.Api.Authorization;
using EventWOS.Application.Approval.Commands;
using EventWOS.Application.Approval.DTOs;
using EventWOS.Application.Approval.Queries;
using EventWOS.Domain.Enums;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Common;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace EventWOS.Api.Controllers;

/// <summary>
/// Approval queue for self-registered Vendors and Crew. Restricted to
/// Admin + Manager. Uses [Permission("users:status")] — same gate
/// already used by ChangeUserStatus, so no new permission slugs to seed.
///
/// Endpoint shape follows the project's pattern from rule #29:
/// [Permission] attribute + explicit role check inside the action body
/// to avoid the silent-403 footgun of bare [Authorize(Roles=...)].
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/admin/approval-queue")]
[Produces("application/json")]
public sealed class ApprovalController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ICurrentUser _currentUser;

    public ApprovalController(IMediator mediator, ICurrentUser currentUser)
    {
        _mediator = mediator;
        _currentUser = currentUser;
    }

    [Permission("users:status")]
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<ApprovalQueueDto>), 200)]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        if (_currentUser.Role is not UserRole.Admin and not UserRole.Manager) return Forbid();
        var result = await _mediator.Send(new GetApprovalQueueQuery(), ct);
        return Ok(ApiResponse<ApprovalQueueDto>.Ok(result.Value));
    }

    [Permission("users:status")]
    [HttpPost("{userId:guid}/approve")]
    [ProducesResponseType(typeof(ApiResponse<ApproveUserResponse>), 200)]
    [ProducesResponseType(typeof(ApiResponse), 404)]
    [ProducesResponseType(typeof(ApiResponse), 409)]
    public async Task<IActionResult> Approve(Guid userId, CancellationToken ct)
    {
        if (_currentUser.Role is not UserRole.Admin and not UserRole.Manager) return Forbid();

        var cmd = new ApproveUserCommand(userId, _currentUser.UserId!.Value);
        var result = await _mediator.Send(cmd, ct);
        if (result.IsFailure)
        {
            var status = result.Error.Code switch
            {
                "User.NotFound"        => 404,
                "Approval.NotPending"  => 409,
                _ => 400
            };
            return StatusCode(status, ApiResponse<ApproveUserResponse>.Fail(result.Error.Message));
        }
        return Ok(ApiResponse<ApproveUserResponse>.Ok(result.Value));
    }

    [Permission("users:status")]
    [HttpPost("{userId:guid}/reject")]
    [ProducesResponseType(typeof(ApiResponse), 200)]
    public async Task<IActionResult> Reject(Guid userId, [FromBody] RejectDto dto, CancellationToken ct)
    {
        if (_currentUser.Role is not UserRole.Admin and not UserRole.Manager) return Forbid();

        var cmd = new RejectUserCommand(userId, _currentUser.UserId!.Value, dto.Reason);
        var result = await _mediator.Send(cmd, ct);
        if (result.IsFailure)
        {
            var status = result.Error.Code switch
            {
                "User.NotFound"           => 404,
                "Approval.NotPending"     => 409,
                "Approval.ReasonRequired" => 400,
                _ => 400
            };
            return StatusCode(status, ApiResponse.Fail(result.Error.Message));
        }
        return Ok(ApiResponse.Ok("Registration rejected."));
    }
}

public sealed record RejectDto(string Reason);
