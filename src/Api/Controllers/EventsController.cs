using EventWOS.Api.Authorization;
using Asp.Versioning;
using EventWOS.Application.Attendance.Commands;
using EventWOS.Application.Events.Commands;
using EventWOS.Application.Events.DTOs;
using EventWOS.Application.Events.Queries;
using EventWOS.Application.Attendance.DTOs;
using EventWOS.Domain.Enums;
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

    // ── Events CRUD ───────────────────────────────────────────────────────────

    /// <summary>List events with optional filters.</summary>
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

    /// <summary>Get event by ID.</summary>
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
        if (!_currentUser.HasPermission("events:write")) return Forbid();

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
        if (!_currentUser.HasPermission("events:write")) return Forbid();

        var result = await _mediator.Send(new UpdateEventCommand(
            id, req.Title, req.Description, req.Venue, req.Address,
            req.StartAt, req.EndAt, req.MaxCrew), ct);

        return result.IsSuccess ? Ok(ApiResponse.Ok("Event updated.")) : BadRequest(ApiResponse.Fail(result.Error.Message));
    }

    /// <summary>Change event status (publish/start/complete/cancel).</summary>
    [Permission("events:write")]
    [HttpPatch("{id:guid}/status")]
    public async Task<IActionResult> ChangeStatus(Guid id, [FromBody] ChangeEventStatusRequest req, CancellationToken ct)
    {
        if (!_currentUser.HasPermission("events:write")) return Forbid();

        var result = await _mediator.Send(new ChangeEventStatusCommand(id, req.Action, req.Reason), ct);
        return result.IsSuccess ? Ok(ApiResponse.Ok()) : BadRequest(ApiResponse.Fail(result.Error.Message));
    }

    // ── Assignments ───────────────────────────────────────────────────────────

    /// <summary>List assignments for an event.</summary>
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

    /// <summary>Assign a crew member to an event.</summary>
    [Permission("events:write")]
    [HttpPost("{id:guid}/assignments")]
    public async Task<IActionResult> AssignCrew(Guid id, [FromBody] AssignCrewRequest req, CancellationToken ct)
    {
        if (!_currentUser.HasPermission("events:write")) return Forbid();

        var result = await _mediator.Send(new AssignCrewCommand(
            id, req.CrewId, req.VendorId, _currentUser.UserId!.Value), ct);

        return result.IsSuccess
            ? Created(string.Empty, ApiResponse<EventAssignmentDto>.Ok(result.Value))
            : BadRequest(ApiResponse<EventAssignmentDto>.Fail(result.Error.Message));
    }

    /// <summary>Crew responds to their assignment (confirm/decline).</summary>
    [Permission("events:write")]
    [HttpPatch("assignments/{assignmentId:guid}/respond")]
    public async Task<IActionResult> RespondAssignment(Guid assignmentId, [FromBody] RespondAssignmentRequest req, CancellationToken ct)
    {
        if (_currentUser.Role != UserRole.Crew) return Forbid();

        var result = await _mediator.Send(new RespondAssignmentCommand(
            assignmentId, _currentUser.UserId!.Value, req.Response, req.Reason), ct);

        return result.IsSuccess ? Ok(ApiResponse.Ok()) : BadRequest(ApiResponse.Fail(result.Error.Message));
    }

    /// <summary>Get current crew member's assignments.</summary>
    [Permission("events:read")]
    [HttpGet("my-assignments")]
    public async Task<IActionResult> GetMyAssignments(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        var result = await _mediator.Send(new GetMyAssignmentsQuery(_currentUser.UserId!.Value, page, pageSize), ct);
        return Ok(ApiResponse<PagedAssignmentResult>.Ok(result.Value));
    }

    /// <summary>Get all crew assignments that belong to the authenticated vendor.</summary>
    [Permission("events:read")]
    [HttpGet("vendor-assignments")]
    public async Task<IActionResult> GetVendorAssignments(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        // Any authenticated user can call this — query filters by their own VendorId.
        // Non-vendors simply get 0 results (safe, no data leakage).
        var result = await _mediator.Send(
            new GetVendorAssignmentsQuery(_currentUser.UserId!.Value, page, pageSize), ct);
        return Ok(ApiResponse<PagedAssignmentResult>.Ok(result.Value));
    }

    // ── Attendance ────────────────────────────────────────────────────────────

    /// <summary>Record check-in or check-out for an assignment.</summary>
    [Permission("events:write")]
    [HttpPost("assignments/{assignmentId:guid}/attendance")]
    public async Task<IActionResult> RecordAttendance(Guid assignmentId, [FromBody] RecordAttendanceRequest req, CancellationToken ct)
    {
        var result = await _mediator.Send(new RecordAttendanceCommand(
            assignmentId, req.Action, req.Location, _currentUser.UserId!.Value.ToString()), ct);

        return result.IsSuccess
            ? Ok(ApiResponse<AttendanceRecordDto>.Ok(result.Value))
            : BadRequest(ApiResponse<AttendanceRecordDto>.Fail(result.Error.Message));
    }

    /// <summary>Get attendance summary for an event.</summary>
    [Permission("events:read")]
    [HttpGet("{id:guid}/attendance")]
    public async Task<IActionResult> GetAttendance(Guid id, CancellationToken ct)
    {
        if (!_currentUser.HasPermission("events:read")) return Forbid();
        var result = await _mediator.Send(new GetAttendanceSummaryQuery(id), ct);
        return result.IsSuccess
            ? Ok(ApiResponse<AttendanceSummaryDto>.Ok(result.Value))
            : NotFound(ApiResponse<AttendanceSummaryDto>.Fail(result.Error.Message));
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
public sealed record AssignCrewRequest(Guid CrewId, Guid VendorId);
public sealed record RespondAssignmentRequest(string Response, string? Reason = null);
public sealed record RecordAttendanceRequest(string Action, string? Location = null);
