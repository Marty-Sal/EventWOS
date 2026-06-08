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
/// Unified approval queue for self-registered Vendors and Crew.
///   - Admin / Manager (perm: users:status) → can approve VENDOR registrations.
///   - Vendor (perm: crew:approve)          → can approve CREW that
///     self-registered using THEIR referral code.
/// Handler enforces the same scoping in case the controller is reused
/// elsewhere — defence in depth.
///
/// Route renamed from /admin/approval-queue → /approval-queue because
/// it's no longer admin-only.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/approval-queue")]
[Produces("application/json")]
public sealed class ApprovalController : ControllerBase
{
    private readonly IMediator    _mediator;
    private readonly ICurrentUser _currentUser;

    public ApprovalController(IMediator mediator, ICurrentUser currentUser)
    {
        _mediator    = mediator;
        _currentUser = currentUser;
    }

    /// <summary>Returns the queue scoped to caller's role.</summary>
    [Permission("profile:read")]   // every authenticated role has this
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<ApprovalQueueDto>), 200)]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        if (_currentUser.Role is not UserRole.Admin
                                and not UserRole.Manager
                                and not UserRole.Vendor)
            return Forbid();

        var result = await _mediator.Send(new GetApprovalQueueQuery(), ct);
        return Ok(ApiResponse<ApprovalQueueDto>.Ok(result.Value));
    }

    [HttpPost("{userId:guid}/approve")]
    [ProducesResponseType(typeof(ApiResponse<ApproveUserResponse>), 200)]
    [ProducesResponseType(typeof(ApiResponse), 404)]
    [ProducesResponseType(typeof(ApiResponse), 409)]
    public async Task<IActionResult> Approve(Guid userId, CancellationToken ct)
    {
        if (!CanApproveOrReject()) return Forbid();

        var cmd = new ApproveUserCommand(userId, _currentUser.UserId!.Value);
        var result = await _mediator.Send(cmd, ct);
        if (result.IsFailure)
        {
            var status = result.Error.Code switch
            {
                "User.NotFound"        => 404,
                "Approval.NotPending"  => 409,
                "Approval.Forbidden"   => 403,
                _ => 400
            };
            return StatusCode(status, ApiResponse<ApproveUserResponse>.Fail(result.Error.Message));
        }
        return Ok(ApiResponse<ApproveUserResponse>.Ok(result.Value));
    }

    [HttpPost("{userId:guid}/reject")]
    [ProducesResponseType(typeof(ApiResponse), 200)]
    public async Task<IActionResult> Reject(Guid userId, [FromBody] RejectDto dto, CancellationToken ct)
    {
        if (!CanApproveOrReject()) return Forbid();

        var cmd = new RejectUserCommand(userId, _currentUser.UserId!.Value, dto.Reason);
        var result = await _mediator.Send(cmd, ct);
        if (result.IsFailure)
        {
            var status = result.Error.Code switch
            {
                "User.NotFound"           => 404,
                "Approval.NotPending"     => 409,
                "Approval.ReasonRequired" => 400,
                "Approval.Forbidden"      => 403,
                _ => 400
            };
            return StatusCode(status, ApiResponse.Fail(result.Error.Message));
        }
        return Ok(ApiResponse.Ok("Registration rejected."));
    }

    private bool CanApproveOrReject()
    {
        // Permission check is done by the handler (which knows the role).
        // Here we only short-circuit unauthorized roles entirely.
        return _currentUser.Role is UserRole.Admin
                                 or UserRole.Manager
                                 or UserRole.Vendor;
    }
}

public sealed record RejectDto(string Reason);
