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

    /// <summary>Update payment status: approve | pay | reject | hold.</summary>
    [Permission("payments:write")]
    [HttpPatch("{id:guid}/status")]
    public async Task<IActionResult> UpdatePaymentStatus(
        Guid id, [FromBody] UpdatePaymentStatusRequest req, CancellationToken ct = default)
    {
        var result = await _mediator.Send(new UpdatePaymentStatusCommand(
            id, req.Action, req.PaidAmount, req.Method, req.TransactionRef, req.Reason), ct);

        if (!result.IsSuccess)
            return BadRequest(ApiResponse<object>.Fail(result.Error.Message));

        return Ok(ApiResponse<bool>.Ok(true));
    }

    // ── Payroll Batches ──────────────────────────────────────────────────────

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

        return Ok(ApiResponse<PagedResult<CrewPaymentDto>>.Ok(result.Value));
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
