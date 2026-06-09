using EventWOS.Api.Authorization;
using Asp.Versioning;
using EventWOS.Application.Payments.Commands;
using EventWOS.Application.Payments.DTOs;
using EventWOS.Application.Payments.Queries;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Common;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EventWOS.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/payments")]
[ApiVersion("1.0")]
[Authorize]
public sealed class PaymentsController : ControllerBase
{
    private readonly IMediator    _mediator;
    private readonly ICurrentUser _currentUser;

    public PaymentsController(IMediator mediator, ICurrentUser currentUser)
    {
        _mediator    = mediator;
        _currentUser = currentUser;
    }

    // ── Crew Payments ────────────────────────────────────────────────────────

    /// <summary>List crew payments (filterable by event/vendor/crew/status).</summary>
    [Permission("payments:read")]
    [HttpGet]
    public async Task<IActionResult> GetPayments(
        [FromQuery] Guid?   eventId,
        [FromQuery] Guid?   vendorId,
        [FromQuery] Guid?   crewId,
        [FromQuery] string? status,
        [FromQuery] int     page     = 1,
        [FromQuery] int     pageSize = 20,
        CancellationToken ct = default)
    {
        // Vendor scoping: vendors can only see payments for their own crew.
        // Force vendorId = their own userId regardless of what the client sent.
        if (_currentUser.Role == EventWOS.Domain.Enums.UserRole.Vendor)
            vendorId = _currentUser.UserId;

        var result = await _mediator.Send(
            new GetPaymentsQuery(eventId, vendorId, crewId, status, page, pageSize), ct);
        return Ok(ApiResponse<PagedResult<CrewPaymentDto>>.Ok(result.Value));
    }

    /// <summary>Create a crew payment record for an assignment.</summary>
    [Permission("payments:write")]
    [HttpPost]
    public async Task<IActionResult> CreatePayment(
        [FromBody] CreatePaymentRequest req, CancellationToken ct = default)
    {
        var result = await _mediator.Send(new CreateCrewPaymentCommand(
            req.EventId, req.AssignmentId, req.CrewId, req.VendorId,
            req.AgreedAmount, req.Notes), ct);

        if (!result.IsSuccess)
            return BadRequest(ApiResponse<object>.Fail(result.Error.Message));

        return Ok(ApiResponse<Guid>.Ok(result.Value));
    }

    /// <summary>
    /// Update payment status. The action determines who is allowed:
    ///   approve / reject / hold   → payments:write   (Admin / Manager)
    ///   pay                       → payments:write   OR  vendor owns the payment + payments:disburse
    ///   ack-received / ack-pending → crew owns the payment + payments:acknowledge
    /// Ownership checks happen inside the command handler (it owns the data).
    /// </summary>
    [HttpPatch("{id:guid}/status")]
    public async Task<IActionResult> UpdatePaymentStatus(
        Guid id, [FromBody] UpdatePaymentStatusRequest req, CancellationToken ct = default)
    {
        var action = (req.Action ?? "").ToLowerInvariant();

        // Coarse permission gate. The command does fine-grained ownership.
        var hasPerm = action switch
        {
            "approve" or "reject" or "hold" => _currentUser.HasPermission("payments:write"),
            "pay"                           => _currentUser.HasPermission("payments:write")
                                            || _currentUser.HasPermission("payments:disburse"),
            "ack-received" or "ack-pending" => _currentUser.HasPermission("payments:acknowledge"),
            _                               => false
        };
        if (!hasPerm) return Forbid();

        var result = await _mediator.Send(new UpdatePaymentStatusCommand(
            id, action, req.PaidAmount, req.Method, req.TransactionRef, req.Reason,
            _currentUser.UserId,
            _currentUser.HasPermission("payments:write")), ct);

        if (!result.IsSuccess)
            return BadRequest(ApiResponse<object>.Fail(result.Error.Message));

        return Ok(ApiResponse<bool>.Ok(true));
    }

    // ── Payroll Batches ──────────────────────────────────────────────────────

    /// <summary>
    /// List every payable party for an event (vendors with attended crew +
    /// direct-assigned crew who attended). Drives the event-centric "New
    /// Payroll Batch" dialog.
    /// </summary>
    [Permission("payments:write")]
    [HttpGet("event/{eventId:guid}/payable-roster")]
    public async Task<IActionResult> GetEventPayableRoster(
        Guid eventId, CancellationToken ct = default)
    {
        var result = await _mediator.Send(new GetEventPayableRosterQuery(eventId), ct);
        if (!result.IsSuccess)
            return BadRequest(ApiResponse<object>.Fail(result.Error.Message));
        return Ok(ApiResponse<EventPayableRosterDto>.Ok(result.Value!));
    }

    /// <summary>
    /// Create a payroll batch from per-party amounts typed against the event's
    /// payable roster. Creates one batch per non-zero line.
    /// </summary>
    [Permission("payments:write")]
    [HttpPost("event/{eventId:guid}/payroll")]
    public async Task<IActionResult> CreateEventPayrollBatch(
        Guid eventId,
        [FromBody] CreateEventPayrollBatchRequest req,
        CancellationToken ct = default)
    {
        var cmd = new CreateEventPayrollBatchCommand(
            eventId,
            req.Lines.Select(l => new EventPayrollBatchLine(l.Kind, l.PartyId, l.Amount)).ToList(),
            req.Notes);
        var result = await _mediator.Send(cmd, ct);
        if (!result.IsSuccess)
            return BadRequest(ApiResponse<object>.Fail(result.Error.Message));
        return Ok(ApiResponse<EventPayrollBatchResult>.Ok(result.Value!));
    }

    /// <summary>List payroll batches.</summary>
    [Permission("payments:read")]
    [HttpGet("payroll")]
    public async Task<IActionResult> GetPayrollBatches(
        [FromQuery] Guid?   vendorId,
        [FromQuery] Guid?   eventId,
        [FromQuery] string? status,
        [FromQuery] int     page     = 1,
        [FromQuery] int     pageSize = 20,
        CancellationToken ct = default)
    {
        var result = await _mediator.Send(
            new GetPayrollBatchesQuery(vendorId, eventId, status, page, pageSize), ct);
        return Ok(ApiResponse<PagedResult<PayrollBatchDto>>.Ok(result.Value));
    }

    /// <summary>Create a payroll batch grouping multiple payments.</summary>
    [Permission("payments:write")]
    [HttpPost("payroll")]
    public async Task<IActionResult> CreatePayrollBatch(
        [FromBody] CreatePayrollBatchRequest req, CancellationToken ct = default)
    {
        var result = await _mediator.Send(new CreatePayrollBatchCommand(
            req.VendorId, req.EventId, req.Notes,
            req.PaymentIds ?? Array.Empty<Guid>(),
            req.DefaultAmountPerCrew), ct);

        if (!result.IsSuccess)
            return BadRequest(ApiResponse<object>.Fail(result.Error.Message));

        return Ok(ApiResponse<Guid>.Ok(result.Value));
    }

    /// <summary>Update payroll batch status: submit | approve | disburse | reject.</summary>
    [Permission("payments:write")]
    [HttpPatch("payroll/{id:guid}/status")]
    public async Task<IActionResult> UpdatePayrollStatus(
        Guid id, [FromBody] UpdatePayrollStatusRequest req, CancellationToken ct = default)
    {
        var actorId = _currentUser.UserId ?? Guid.Empty;
        var result  = await _mediator.Send(new UpdatePayrollStatusCommand(
            id, req.Action, actorId, req.Reason), ct);

        if (!result.IsSuccess)
            return BadRequest(ApiResponse<object>.Fail(result.Error.Message));

        return Ok(ApiResponse<bool>.Ok(true));
    }

    // ── Crew: view their own payment records ──────────────────────────────────

    /// <summary>
    /// Returns the authenticated crew member's payment records.
    /// Requires payments:self permission (auto-assigned to Crew role).
    ///
    /// Phase D step 25: amounts are REDACTED in this response. Crew never
    /// sees AgreedAmount, PaidAmount, BatchTotal or TransactionRef — they
    /// only confirm receipt. Redaction happens here at the controller so a
    /// curious crew member with browser devtools can't pull the numbers
    /// from the JSON payload. The underlying entity/DTO is unchanged so
    /// admin/manager/vendor queries through the same DTO are unaffected.
    /// </summary>
    [Permission("payments:self")]
    [HttpGet("my")]
    public async Task<IActionResult> GetMyPayments(
        [FromQuery] Guid?   eventId  = null,
        [FromQuery] string? status   = null,
        [FromQuery] int     page     = 1,
        [FromQuery] int     pageSize = 20,
        CancellationToken ct = default)
    {
        var crewId = _currentUser.UserId;
        if (crewId is null) return Unauthorized();

        var result = await _mediator.Send(
            new GetPaymentsQuery(eventId, null, crewId, status, page, pageSize), ct);

        // Redact every amount before serialising. Strings & GUIDs & status
        // & acknowledgement & method/date stay (crew needs them for ack flow).
        // PagedResult<T> is a class (not a record), so we rebuild it via its
        // Create factory rather than using `with`.
        var redactedItems = result.Value!.Items
            .Select(p => p with
            {
                AgreedAmount   = 0m,
                PaidAmount     = null,
                BatchTotal     = null,
                TransactionRef = null
            })
            .ToList();

        var redacted = PagedResult<CrewPaymentDto>.Create(
            redactedItems,
            result.Value.TotalCount,
            result.Value.PageNumber,
            result.Value.PageSize);

        return Ok(ApiResponse<PagedResult<CrewPaymentDto>>.Ok(redacted));
    }
}

// ── Request DTOs ─────────────────────────────────────────────────────────────

public sealed record CreatePaymentRequest(
    Guid    EventId,
    Guid    AssignmentId,
    Guid    CrewId,
    Guid    VendorId,
    decimal AgreedAmount,
    string? Notes
);

public sealed record UpdatePaymentStatusRequest(
    string   Action,
    decimal? PaidAmount,
    string?  Method,
    string?  TransactionRef,
    string?  Reason
);

public sealed record CreatePayrollBatchRequest(
    Guid     VendorId,
    Guid     EventId,
    string?  Notes,
    IReadOnlyList<Guid> PaymentIds,
    decimal? DefaultAmountPerCrew = null
);

public sealed record UpdatePayrollStatusRequest(string Action, string? Reason);


public sealed record CreateEventPayrollBatchRequest(
    IReadOnlyList<EventPayrollBatchLineRequest> Lines,
    string? Notes
);

public sealed record EventPayrollBatchLineRequest(
    string  Kind,
    Guid    PartyId,
    decimal Amount
);
