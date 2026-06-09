using Asp.Versioning;
using EventWOS.Api.Authorization;
using EventWOS.Api.Excel;
using EventWOS.Application.Attendance.Commands;
using EventWOS.Application.Attendance.DTOs;
using EventWOS.Application.Attendance.Queries;
using EventWOS.Application.Events.DTOs;
using EventWOS.Application.Events.Queries;
using EventWOS.Domain.Enums;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Common;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EventWOS.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/attendance")]
[ApiVersion("1.0")]
[Authorize]
public sealed class AttendanceController : ControllerBase
{
    private readonly IMediator    _mediator;
    private readonly ICurrentUser _currentUser;

    public AttendanceController(IMediator mediator, ICurrentUser currentUser)
    {
        _mediator    = mediator;
        _currentUser = currentUser;
    }

    /// <summary>
    /// List all attendance records — filterable + sortable. (attendance:read)
    ///
    /// Phase D step 22: expanded from (eventId, crewId) to a real filter
    /// bar: search, action, date range, sort. Old signature still works
    /// because every new param is optional with a sensible default.
    /// </summary>
    [Permission("attendance:read")]
    [HttpGet]
    public async Task<IActionResult> GetList(
        [FromQuery] Guid?     eventId  = null,
        [FromQuery] Guid?     crewId   = null,
        [FromQuery] string?   search   = null,
        [FromQuery] string?   action   = null,
        [FromQuery] DateTime? from     = null,
        [FromQuery] DateTime? to       = null,
        [FromQuery] string?   sortBy   = "RecordedAt",
        [FromQuery] bool      sortDesc = true,
        [FromQuery] int       page     = 1,
        [FromQuery] int       pageSize = 20,
        CancellationToken     ct       = default)
    {
        var result = await _mediator.Send(new GetAttendanceListQuery(
            eventId, crewId, search, action, from, to, sortBy, sortDesc, page, pageSize), ct);
        return result.IsSuccess
            ? Ok(ApiResponse<PagedResult<AttendanceListItemDto>>.Ok(result.Value))
            : BadRequest(ApiResponse<PagedResult<AttendanceListItemDto>>.Fail(result.Error.Message));
    }

    /// <summary>
    /// Phase D step 22: cross-event attendance summary (one row per
    /// event with check-in / no-show / override counts). (attendance:read)
    /// </summary>
    [Permission("attendance:read")]
    [HttpGet("summary/all")]
    public async Task<IActionResult> GetOverallSummary(
        [FromQuery] string?      search   = null,
        [FromQuery] EventStatus? status   = null,
        [FromQuery] DateTime?    from     = null,
        [FromQuery] DateTime?    to       = null,
        [FromQuery] string?      sortBy   = "StartAt",
        [FromQuery] bool         sortDesc = true,
        [FromQuery] int          page     = 1,
        [FromQuery] int          pageSize = 20,
        CancellationToken        ct       = default)
    {
        var result = await _mediator.Send(new GetAttendanceOverallSummaryQuery(
            search, status, from, to, sortBy, sortDesc, page, pageSize), ct);
        return result.IsSuccess
            ? Ok(ApiResponse<PagedResult<EventAttendanceSummaryRow>>.Ok(result.Value))
            : BadRequest(ApiResponse<PagedResult<EventAttendanceSummaryRow>>.Fail(result.Error.Message));
    }

    /// <summary>
    /// Phase D step 22: Excel download of the Logs tab. Honours the same
    /// filters as the on-screen view — what the admin sees is what they
    /// get. Pagination is bypassed (All=true) to export everything that
    /// matches. (attendance:read)
    /// </summary>
    [Permission("attendance:read")]
    [HttpGet("export")]
    public async Task<IActionResult> ExportLogs(
        [FromQuery] Guid?     eventId  = null,
        [FromQuery] Guid?     crewId   = null,
        [FromQuery] string?   search   = null,
        [FromQuery] string?   action   = null,
        [FromQuery] DateTime? from     = null,
        [FromQuery] DateTime? to       = null,
        [FromQuery] string?   sortBy   = "RecordedAt",
        [FromQuery] bool      sortDesc = true,
        CancellationToken     ct       = default)
    {
        var result = await _mediator.Send(new GetAttendanceListQuery(
            eventId, crewId, search, action, from, to, sortBy, sortDesc, 1, int.MaxValue, true), ct);
        if (!result.IsSuccess)
            return BadRequest(ApiResponse.Fail(result.Error.Message));

        var (bytes, mime, name) = AttendanceExcelExporter.ExportLogs(result.Value.Items);
        return File(bytes, mime, name);
    }

    /// <summary>
    /// Phase D step 22: Excel download of the Summary tab. (attendance:read)
    /// </summary>
    [Permission("attendance:read")]
    [HttpGet("summary/all/export")]
    public async Task<IActionResult> ExportSummary(
        [FromQuery] string?      search   = null,
        [FromQuery] EventStatus? status   = null,
        [FromQuery] DateTime?    from     = null,
        [FromQuery] DateTime?    to       = null,
        [FromQuery] string?      sortBy   = "StartAt",
        [FromQuery] bool         sortDesc = true,
        CancellationToken        ct       = default)
    {
        var result = await _mediator.Send(new GetAttendanceOverallSummaryQuery(
            search, status, from, to, sortBy, sortDesc, 1, int.MaxValue, true), ct);
        if (!result.IsSuccess)
            return BadRequest(ApiResponse.Fail(result.Error.Message));

        var (bytes, mime, name) = AttendanceExcelExporter.ExportSummary(result.Value.Items);
        return File(bytes, mime, name);
    }

    /// <summary>Attendance summary (stats + crew breakdown) for a specific event. (attendance:read)</summary>
    [Permission("attendance:read")]
    [HttpGet("events/{eventId:guid}/summary")]
    public async Task<IActionResult> GetEventSummary(Guid eventId, CancellationToken ct = default)
    {
        var result = await _mediator.Send(new GetAttendanceSummaryQuery(eventId), ct);
        return result.IsSuccess
            ? Ok(ApiResponse<AttendanceSummaryDto>.Ok(result.Value))
            : NotFound(ApiResponse<AttendanceSummaryDto>.Fail(result.Error.Message));
    }

    /// <summary>Record a check-in or check-out for an assignment. (attendance:write)</summary>
    [Permission("attendance:write")]
    [HttpPost("assignments/{assignmentId:guid}")]
    public async Task<IActionResult> RecordAttendance(
        Guid assignmentId,
        [FromBody] AttendanceActionRequest req,
        CancellationToken ct = default)
    {
        var result = await _mediator.Send(
            new RecordAttendanceCommand(assignmentId, req.Action, req.Location,
                _currentUser.UserId?.ToString()), ct);
        return result.IsSuccess
            ? Ok(ApiResponse<AttendanceRecordDto>.Ok(result.Value))
            : BadRequest(ApiResponse<AttendanceRecordDto>.Fail(result.Error.Message));
    }

    /// <summary>My own attendance records (all authenticated users).</summary>
    [HttpGet("my")]
    public async Task<IActionResult> GetMy(
        [FromQuery] int page     = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct     = default)
    {
        if (_currentUser.UserId is null) return Unauthorized();
        var result = await _mediator.Send(new GetMyAttendanceQuery(_currentUser.UserId.Value, page, pageSize), ct);
        return result.IsSuccess
            ? Ok(ApiResponse<PagedResult<AttendanceListItemDto>>.Ok(result.Value))
            : BadRequest(ApiResponse<PagedResult<AttendanceListItemDto>>.Fail(result.Error.Message));
    }
}

public sealed record AttendanceActionRequest(string Action, string? Location = null);
