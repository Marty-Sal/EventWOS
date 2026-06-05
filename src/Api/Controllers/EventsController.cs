using EventWOS.Api.Authorization;
using Asp.Versioning;
using EventWOS.Application.Attendance.Commands;
using EventWOS.Application.Events.Commands;
using EventWOS.Application.CrewGroups.Commands;
using EventWOS.Application.CrewGroups.DTOs;
using EventWOS.Domain.Enums;
using EventWOS.Application.Events.DTOs;
using EventWOS.Application.Events.Queries;
using EventWOS.Application.Attendance.DTOs;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Common;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EventWOS.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/events")]
[Authorize]
[Produces("application/json")]
public sealed class EventsController : ControllerBase
{
    private readonly IMediator    _mediator;
    private readonly ICurrentUser _currentUser;

    public EventsController(IMediator mediator, ICurrentUser currentUser)
    {
        _mediator    = mediator;
        _currentUser = currentUser;
    }

    // ── Events CRUD (Admin / Manager only) ────────────────────────────────────

    /// <summary>List ALL events. Requires events:read (Admin/Manager).</summary>
    [Permission("events:read")]
    [HttpGet]
    public async Task<IActionResult> GetEvents(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20,
        [FromQuery] string? search = null,
        [FromQuery] EventStatus? status = null,
        [FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null,
        CancellationToken ct = default)
    {
        var result = await _mediator.Send(new GetEventsQuery(page, pageSize, search, status, from, to), ct);
        return Ok(ApiResponse<PagedEventResult>.Ok(result.Value));
    }

    /// <summary>Get event by ID. Requires events:read (Admin/Manager).</summary>
    [Permission("events:read")]
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetEvent(Guid id, CancellationToken ct)
    {
        var result = await _mediator.Send(new GetEventByIdQuery(id), ct);
        return result.IsSuccess
            ? Ok(ApiResponse<EventDto>.Ok(result.Value))
            : NotFound(ApiResponse<EventDto>.Fail(result.Error.Message));
    }

    /// <summary>Create event. Admin/Manager only.</summary>
    [Permission("events:write")]
    [HttpPost]
    public async Task<IActionResult> CreateEvent([FromBody] CreateEventRequest req, CancellationToken ct)
    {
        var result = await _mediator.Send(new CreateEventCommand(
            req.Title, req.Description, req.Venue, req.Address,
            req.StartAt, req.EndAt, req.MaxCrew, _currentUser.UserId!.Value), ct);

        return result.IsSuccess
            ? CreatedAtAction(nameof(GetEvent), new { id = result.Value.Id, version = "1" },
                ApiResponse<EventDto>.Ok(result.Value))
            : BadRequest(ApiResponse<EventDto>.Fail(result.Error.Message));
    }

    /// <summary>Update event. Admin/Manager only.</summary>
    [Permission("events:write")]
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateEvent(Guid id, [FromBody] UpdateEventRequest req, CancellationToken ct)
    {
        var result = await _mediator.Send(new UpdateEventCommand(
            id, req.Title, req.Description, req.Venue, req.Address,
            req.StartAt, req.EndAt, req.MaxCrew), ct);

        return result.IsSuccess
            ? Ok(ApiResponse.Ok("Event updated."))
            : BadRequest(ApiResponse.Fail(result.Error.Message));
    }

    /// <summary>Change event status (publish/start/complete/cancel). Admin/Manager only.</summary>
    [Permission("events:write")]
    [HttpPatch("{id:guid}/status")]
    public async Task<IActionResult> ChangeStatus(Guid id, [FromBody] ChangeEventStatusRequest req, CancellationToken ct)
    {
        var result = await _mediator.Send(new ChangeEventStatusCommand(id, req.Action, req.Reason), ct);
        return result.IsSuccess ? Ok(ApiResponse.Ok()) : BadRequest(ApiResponse.Fail(result.Error.Message));
    }

    // ── Assignments (Admin / Manager) ─────────────────────────────────────────

    /// <summary>List assignments for an event. Requires events:read.</summary>
    [Permission("events:read")]
    [HttpGet("{id:guid}/assignments")]
    public async Task<IActionResult> GetAssignments(
        Guid id,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50,
        [FromQuery] AssignmentStatus? status = null,
        CancellationToken ct = default)
    {
        var result = await _mediator.Send(new GetEventAssignmentsQuery(id, page, pageSize, status), ct);
        return Ok(ApiResponse<PagedAssignmentResult>.Ok(result.Value));
    }

    /// <summary>Assign a crew member to an event. Requires events:write.</summary>
    [Permission("events:write")]
    [HttpPost("{id:guid}/assignments")]
    public async Task<IActionResult> AssignCrew(Guid id, [FromBody] AssignCrewRequest req, CancellationToken ct)
    {
        var result = await _mediator.Send(new AssignCrewCommand(
            id, req.CrewId, req.VendorId, _currentUser.UserId!.Value), ct);

        return result.IsSuccess
            ? Created(string.Empty, ApiResponse<EventAssignmentDto>.Ok(result.Value))
            : BadRequest(ApiResponse<EventAssignmentDto>.Fail(result.Error.Message));
    }

    // ── Crew self-service endpoints (profile:read / profile:write — all roles) ─

    /// <summary>
    /// Get assignments for the authenticated crew member.
    /// Uses profile:read so Crew (who have no events:read) can call this.
    /// </summary>
    [Permission("profile:read")]
    [HttpGet("my-assignments")]
    public async Task<IActionResult> GetMyAssignments(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        var result = await _mediator.Send(new GetMyAssignmentsQuery(_currentUser.UserId!.Value, page, pageSize), ct);
        return Ok(ApiResponse<PagedAssignmentResult>.Ok(result.Value));
    }

    /// <summary>
    /// Get events the authenticated crew member is assigned to (their personal event list).
    /// Uses profile:read so Crew can call this without events:read.
    /// </summary>
    [Permission("profile:read")]
    [HttpGet("my-events")]
    public async Task<IActionResult> GetMyEvents(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        var result = await _mediator.Send(new GetMyEventsQuery(_currentUser.UserId!.Value, _currentUser.Role ?? EventWOS.Domain.Enums.UserRole.Crew, page, pageSize), ct);
        return Ok(ApiResponse<PagedEventResult>.Ok(result.Value));
    }

    /// <summary>
    /// Get assignments for the authenticated vendor's crew.
    /// Uses profile:read so Vendor (who may not have events:read) can call this.
    /// </summary>
    [Permission("profile:read")]
    [HttpGet("vendor-assignments")]
    public async Task<IActionResult> GetVendorAssignments(
        [FromQuery] string? mode = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var parsedMode = VendorAssignmentMode.All;
        if (!string.IsNullOrWhiteSpace(mode)
            && Enum.TryParse<VendorAssignmentMode>(mode, ignoreCase: true, out var m))
            parsedMode = m;

        var result = await _mediator.Send(
            new GetVendorAssignmentsQuery(_currentUser.UserId!.Value, parsedMode, page, pageSize), ct);
        return Ok(ApiResponse<PagedAssignmentResult>.Ok(result.Value));
    }

    /// <summary>
    /// Vendor self-service: attach a crew member from the vendor's roster to an
    /// event the vendor has been directly assigned to. Vendor-only mode follow-up.
    /// </summary>
    [Permission("profile:write")]
    [HttpPost("{id:guid}/vendor-assign-crew")]
    public async Task<IActionResult> VendorAssignCrew(Guid id, [FromBody] VendorAssignCrewRequest req, CancellationToken ct)
    {
        if (_currentUser.Role != UserRole.Vendor) return Forbid();

        var result = await _mediator.Send(new VendorAssignCrewCommand(
            id, req.CrewId, _currentUser.UserId!.Value), ct);

        return result.IsSuccess
            ? Created(string.Empty, ApiResponse<EventAssignmentDto>.Ok(result.Value))
            : BadRequest(ApiResponse<EventAssignmentDto>.Fail(result.Error.Message));
    }

    /// <summary>
    /// Vendor self-service: invite every crew member in a saved group to this
    /// event. Returns an aggregate summary (invited / skipped / failed) so the
    /// UI can show what actually happened instead of a single OK / error.
    /// </summary>
    [Permission("profile:write")]
    [HttpPost("{id:guid}/vendor-assign-group")]
    public async Task<IActionResult> VendorAssignGroup(
        Guid id, [FromBody] VendorAssignGroupRequest req, CancellationToken ct)
    {
        if (_currentUser.Role != UserRole.Vendor) return Forbid();

        var result = await _mediator.Send(new VendorAssignGroupCommand(
            id, req.GroupId, _currentUser.UserId!.Value), ct);

        return result.IsSuccess
            ? Ok(ApiResponse<VendorAssignGroupResultDto>.Ok(result.Value))
            : BadRequest(ApiResponse<VendorAssignGroupResultDto>.Fail(result.Error.Message));
    }

    /// <summary>
    /// Vendor revokes a pending crew invitation they sent. Only allowed while
    /// the crew hasn't responded yet (status is still Invited). Mirrors the
    /// manager's DELETE /vendor-invitations/{id} endpoint.
    /// </summary>
    [Permission("profile:write")]
    [HttpDelete("{id:guid}/vendor-crew-invitations/{crewId:guid}")]
    public async Task<IActionResult> VendorRevokeCrewInvite(
        Guid id, Guid crewId, CancellationToken ct)
    {
        if (_currentUser.Role != UserRole.Vendor) return Forbid();

        var result = await _mediator.Send(new VendorRevokeCrewInviteCommand(
            id, crewId, _currentUser.UserId!.Value), ct);

        return result.IsSuccess
            ? Ok(ApiResponse.Ok("Invitation revoked."))
            : BadRequest(ApiResponse.Fail(result.Error.Message));
    }

    /// <summary>
    /// Vendor responds to the Manager's invitation to staff an event (accept / reject).
    /// Operates on the placeholder row only (CrewId == null). Does NOT enter the
    /// Manager approval queue — this is a vendor↔manager decision about the
    /// event itself.
    /// </summary>
    [Permission("profile:write")]
    [HttpPatch("vendor-invitations/{assignmentId:guid}/respond")]
    public async Task<IActionResult> VendorRespondToInvite(
        Guid assignmentId,
        [FromBody] VendorRespondToInviteRequest req,
        CancellationToken ct)
    {
        if (_currentUser.Role != UserRole.Vendor) return Forbid();

        var result = await _mediator.Send(new VendorRespondToInviteCommand(
            assignmentId, _currentUser.UserId!.Value, req.Response, req.Reason), ct);

        return result.IsSuccess
            ? Ok(ApiResponse.Ok())
            : BadRequest(ApiResponse.Fail(result.Error.Message));
    }

    /// <summary>
    /// Manager revokes a pending vendor invitation (vendor hasn't responded yet).
    /// Soft-deletes the placeholder row.
    /// </summary>
    [Permission("events:write")]
    [HttpDelete("vendor-invitations/{assignmentId:guid}")]
    public async Task<IActionResult> RevokeVendorInvite(Guid assignmentId, CancellationToken ct)
    {
        var result = await _mediator.Send(
            new RevokeVendorInviteCommand(assignmentId, _currentUser.UserId!.Value), ct);
        return result.IsSuccess
            ? Ok(ApiResponse.Ok())
            : BadRequest(ApiResponse.Fail(result.Error.Message));
    }

    /// <summary>
    /// Manager re-invites a vendor whose invitation was previously rejected.
    /// </summary>
    [Permission("events:write")]
    [HttpPatch("vendor-invitations/{assignmentId:guid}/reinvite")]
    public async Task<IActionResult> ReinviteVendor(Guid assignmentId, CancellationToken ct)
    {
        var result = await _mediator.Send(
            new ReinviteVendorCommand(assignmentId, _currentUser.UserId!.Value), ct);
        return result.IsSuccess
            ? Ok(ApiResponse.Ok())
            : BadRequest(ApiResponse.Fail(result.Error.Message));
    }

                /// <summary>
    /// Crew responds to an assignment invitation (confirm / decline).
    /// Uses profile:write — Crew always has this permission.
    /// </summary>
    [Permission("profile:write")]
    [HttpPatch("assignments/{assignmentId:guid}/respond")]
    public async Task<IActionResult> RespondAssignment(Guid assignmentId, [FromBody] RespondAssignmentRequest req, CancellationToken ct)
    {
        if (_currentUser.Role != UserRole.Crew) return Forbid();

        var result = await _mediator.Send(new RespondAssignmentCommand(
            assignmentId, _currentUser.UserId!.Value, req.Response, req.Reason), ct);

        return result.IsSuccess ? Ok(ApiResponse.Ok()) : BadRequest(ApiResponse.Fail(result.Error.Message));
    }

    // ── 2-Step Approval Flow ─────────────────────────────────────────────────

    /// <summary>
    /// Manager approval queue — assignments waiting for final Manager decision.
    /// </summary>
    [Permission("crew:approve")]
    [HttpGet("assignments/manager-queue")]
    public async Task<IActionResult> GetManagerQueue(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        var result = await _mediator.Send(new GetManagerApprovalQueueQuery(page, pageSize), ct);
        return Ok(ApiResponse<PagedResult<ManagerApprovalItemDto>>.Ok(result.Value));
    }

    /// <summary>
    /// Vendor approves a crew member → forwards to Manager queue.
    /// </summary>
    [Permission("crew:invite")]
    [HttpPatch("assignments/{assignmentId:guid}/vendor-approve")]
    public async Task<IActionResult> VendorApprove(Guid assignmentId, CancellationToken ct)
    {
        if (_currentUser.Role != UserRole.Vendor) return Forbid();
        var result = await _mediator.Send(
            new VendorApproveAssignmentCommand(assignmentId, _currentUser.UserId!.Value), ct);
        return result.IsSuccess ? Ok(ApiResponse.Ok()) : BadRequest(ApiResponse.Fail(result.Error.Message));
    }

    /// <summary>
    /// Vendor directly forwards an Invited crew member to Manager queue,
    /// bypassing the crew acceptance step (for offline-confirmed crew).
    /// </summary>
    [Permission("crew:invite")]
    [HttpPatch("assignments/{assignmentId:guid}/vendor-direct-forward")]
    public async Task<IActionResult> VendorDirectForward(Guid assignmentId, CancellationToken ct)
    {
        if (_currentUser.Role != UserRole.Vendor) return Forbid();
        var result = await _mediator.Send(
            new VendorDirectForwardCommand(assignmentId, _currentUser.UserId!.Value), ct);
        return result.IsSuccess ? Ok(ApiResponse.Ok()) : BadRequest(ApiResponse.Fail(result.Error.Message));
    }

    /// <summary>
    /// Vendor rejects a crew member — rejection reason is mandatory.
    /// </summary>
    [Permission("crew:invite")]
    [HttpPatch("assignments/{assignmentId:guid}/vendor-reject")]
    public async Task<IActionResult> VendorReject(Guid assignmentId, [FromBody] ReviewDecisionRequest req, CancellationToken ct)
    {
        if (_currentUser.Role != UserRole.Vendor) return Forbid();
        var result = await _mediator.Send(
            new VendorRejectAssignmentCommand(assignmentId, _currentUser.UserId!.Value, req.Reason ?? ""), ct);
        return result.IsSuccess ? Ok(ApiResponse.Ok()) : BadRequest(ApiResponse.Fail(result.Error.Message));
    }

    /// <summary>
    /// Manager gives final approval — crew is now fully confirmed.
    /// </summary>
    [Permission("crew:approve")]
    [HttpPatch("assignments/{assignmentId:guid}/manager-approve")]
    public async Task<IActionResult> ManagerApprove(Guid assignmentId, CancellationToken ct)
    {
        var result = await _mediator.Send(
            new ManagerApproveAssignmentCommand(assignmentId, _currentUser.UserId!.Value), ct);
        return result.IsSuccess ? Ok(ApiResponse.Ok()) : BadRequest(ApiResponse.Fail(result.Error.Message));
    }

    /// <summary>
    /// Manager rejects in final review — rejection reason is mandatory.
    /// </summary>
    [Permission("crew:approve")]
    [HttpPatch("assignments/{assignmentId:guid}/manager-reject")]
    public async Task<IActionResult> ManagerReject(Guid assignmentId, [FromBody] ReviewDecisionRequest req, CancellationToken ct)
    {
        var result = await _mediator.Send(
            new ManagerRejectAssignmentCommand(assignmentId, _currentUser.UserId!.Value, req.Reason ?? ""), ct);
        return result.IsSuccess ? Ok(ApiResponse.Ok()) : BadRequest(ApiResponse.Fail(result.Error.Message));
    }

    // ── Attendance ────────────────────────────────────────────────────────────

    /// <summary>
    /// Record check-in or check-out for an assignment.
    /// Uses profile:write so Crew can self-check-in without attendance:write.
    /// </summary>
    [Permission("profile:write")]
    [HttpPost("assignments/{assignmentId:guid}/attendance")]
    public async Task<IActionResult> RecordAttendance(Guid assignmentId, [FromBody] RecordAttendanceRequest req, CancellationToken ct)
    {
        var result = await _mediator.Send(new RecordAttendanceCommand(
            assignmentId, req.Action, req.Location, _currentUser.UserId!.Value.ToString()), ct);

        return result.IsSuccess
            ? Ok(ApiResponse<AttendanceRecordDto>.Ok(result.Value))
            : BadRequest(ApiResponse<AttendanceRecordDto>.Fail(result.Error.Message));
    }

    /// <summary>Get attendance summary for an event. Requires events:read.</summary>
    [Permission("events:read")]
    [HttpGet("{id:guid}/attendance")]
    public async Task<IActionResult> GetAttendance(Guid id, CancellationToken ct)
    {
        var result = await _mediator.Send(new GetAttendanceSummaryQuery(id), ct);
        return result.IsSuccess
            ? Ok(ApiResponse<AttendanceSummaryDto>.Ok(result.Value))
            : NotFound(ApiResponse<AttendanceSummaryDto>.Fail(result.Error.Message));
    }
    // ── Rate Crew (Vendor) ────────────────────────────────────────────────────
    [HttpPost("assignments/{assignmentId:guid}/rate-crew")]
    [Authorize(Roles = "Vendor")]
    public async Task<IActionResult> RateCrew(Guid assignmentId, [FromBody] RateCrewRequest body, CancellationToken ct)
    {
        var result = await _mediator.Send(new RateCrewCommand(assignmentId, _currentUser.UserId!.Value, body.Rating), ct);
        return result.IsSuccess ? Ok(ApiResponse.Ok("Crew rated successfully."))
                                : BadRequest(ApiResponse.Fail(result.Error.Message));
    }

}

// ── Request DTOs ──────────────────────────────────────────────────────────────
public sealed record CreateEventRequest(
    string Title, string? Description, string Venue, string? Address,
    DateTime StartAt, DateTime EndAt, int MaxCrew = 0);

public sealed record UpdateEventRequest(
    string Title, string? Description, string Venue, string? Address,
    DateTime StartAt, DateTime EndAt, int MaxCrew = 0);

public sealed record ChangeEventStatusRequest(string Action, string? Reason = null);
public sealed record AssignCrewRequest(Guid? CrewId, Guid? VendorId);
public sealed record VendorAssignCrewRequest(Guid CrewId);
public sealed record VendorAssignGroupRequest(Guid GroupId);
public sealed record VendorRespondToInviteRequest(string Response, string? Reason);
public sealed record RespondAssignmentRequest(string Response, string? Reason = null);
public sealed record RecordAttendanceRequest(string Action, string? Location = null);
public sealed record ReviewDecisionRequest(string? Reason = null);
public sealed record RateCrewRequest(decimal Rating);


