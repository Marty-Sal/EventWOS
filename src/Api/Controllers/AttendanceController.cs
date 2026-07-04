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

    // ── QR-verified check-in ────────────────────────────────────────────
    //
    // Two-party handshake:
    //   1. Crew POST /checkin/request      → mints a PendingCheckIn, returns
    //      the code + expiry. Crew's UI encodes the code in a QR.
    //   2. Vendor POST /checkin/verify     → validates the code, writes the
    //      real AttendanceRecord, flips assignment to Attended.
    //
    // Regeneration = call /checkin/request again. The prior Pending row is
    // auto-cancelled so only one live QR exists per assignment at a time.

    /// <summary>Mint (or regenerate) a QR check-in code for the caller's own
    /// assignment. Uses profile:write so a crew user can call it — same
    /// permission the direct-record path uses (see EventsController).</summary>
    [Permission("profile:write")]
    [HttpPost("checkin/request")]
    public async Task<IActionResult> RequestCheckIn(
        [FromBody] CheckInRequestBody body, CancellationToken ct)
    {
        if (_currentUser.UserId is null) return Unauthorized();

        var result = await _mediator.Send(
            new RequestCheckInCommand(body.AssignmentId, _currentUser.UserId.Value), ct);

        return result.IsSuccess
            ? Ok(ApiResponse<PendingCheckInDto>.Ok(result.Value))
            : BadRequest(ApiResponse<PendingCheckInDto>.Fail(result.Error.Message));
    }

    /// <summary>Return the caller's currently-live QR for an assignment, if
    /// any — used by the modal after a page refresh.</summary>
    [Permission("profile:write")]
    [HttpGet("checkin/my/{assignmentId:guid}")]
    public async Task<IActionResult> GetMyLiveCheckIn(
        Guid assignmentId, CancellationToken ct)
    {
        if (_currentUser.UserId is null) return Unauthorized();

        var result = await _mediator.Send(
            new GetMyPendingCheckInQuery(assignmentId, _currentUser.UserId.Value), ct);

        return result.IsSuccess
            ? Ok(ApiResponse<PendingCheckInDto>.Ok(result.Value))
            : NotFound(ApiResponse<PendingCheckInDto>.Fail(result.Error.Message));
    }

    /// <summary>Vendor (or Manager/Admin fallback) scans the QR and posts the
    /// code back to verify + commit the check-in. Requires attendance:verify.</summary>
    [Permission("attendance:verify")]
    [HttpPost("checkin/verify")]
    public async Task<IActionResult> VerifyCheckIn(
        [FromBody] CheckInVerifyBody body, CancellationToken ct)
    {
        if (_currentUser.UserId is null || _currentUser.Role is null)
            return Unauthorized();

        var result = await _mediator.Send(new VerifyCheckInCommand(
            body.Code,
            _currentUser.UserId.Value,
            _currentUser.Role.Value,
            body.Location), ct);

        return result.IsSuccess
            ? Ok(ApiResponse<CheckInVerifyResultDto>.Ok(result.Value))
            : BadRequest(ApiResponse<CheckInVerifyResultDto>.Fail(result.Error.Message));
    }
}

/// <summary>Body for /checkin/request.</summary>
public sealed record CheckInRequestBody(Guid AssignmentId);

/// <summary>Body for /checkin/verify. Location is optional — the vendor's
/// device may or may not share geolocation.</summary>
public sealed record CheckInVerifyBody(string Code, string? Location = null);

public sealed record AttendanceActionRequest(string Action, string? Location = null);
