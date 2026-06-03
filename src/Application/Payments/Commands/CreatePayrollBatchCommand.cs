using EventWOS.Application.Interfaces;
using EventWOS.Domain.Entities;
using EventWOS.Domain.Enums;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Result;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Payments.Commands;

/// <summary>
/// Creates a payroll batch for a vendor on a completed event.
///
/// Two modes are supported:
///
/// 1. EXPLICIT — caller passes a non-empty <see cref="PaymentIds"/> list. The
///    handler verifies they belong to this vendor/event and are unbatched,
///    and folds them into the new batch (the original flow).
///
/// 2. AUTO — caller omits <see cref="PaymentIds"/> (empty) and provides
///    <see cref="DefaultAmountPerCrew"/>. The handler finds every ManagerApproved
///    assignment for this vendor on this event that has at least one CheckIn
///    record and does not yet have a CrewPayment, then materialises the missing
///    payments at the default amount, auto-approves them, and folds them in.
/// </summary>
public sealed record CreatePayrollBatchCommand(
    Guid     VendorId,
    Guid     EventId,
    string?  Notes,
    IReadOnlyList<Guid> PaymentIds,
    decimal? DefaultAmountPerCrew = null
) : IRequest<Result<Guid>>;

public sealed class CreatePayrollBatchValidator : AbstractValidator<CreatePayrollBatchCommand>
{
    public CreatePayrollBatchValidator()
    {
        RuleFor(x => x.VendorId).NotEmpty();
        RuleFor(x => x.EventId).NotEmpty();

        // Need EITHER explicit payment ids OR a default amount to auto-create.
        RuleFor(x => x)
            .Must(c => (c.PaymentIds is { Count: > 0 })
                    || (c.DefaultAmountPerCrew is > 0))
            .WithMessage("Provide payment IDs to bundle, or a default amount to auto-create payments for attended crew.");

        When(x => x.DefaultAmountPerCrew is not null, () =>
        {
            RuleFor(x => x.DefaultAmountPerCrew!.Value)
                .GreaterThan(0).WithMessage("Default amount must be greater than 0.");
        });
    }
}

public sealed class CreatePayrollBatchHandler : IRequestHandler<CreatePayrollBatchCommand, Result<Guid>>
{
    private readonly IAppDbContext       _db;
    private readonly IUnitOfWork         _uow;
    private readonly INotificationPusher _push;

    public CreatePayrollBatchHandler(IAppDbContext db, IUnitOfWork uow, INotificationPusher push)
    {
        _db   = db;
        _uow  = uow;
        _push = push;
    }

    public async Task<Result<Guid>> Handle(CreatePayrollBatchCommand cmd, CancellationToken ct)
    {
        // 1. Event must exist and be Completed.
        var ev = await _db.Events.FindAsync([cmd.EventId], ct);
        if (ev is null)
            return Result.Failure<Guid>(Error.Custom("Payroll.EventNotFound", "Event not found."));
        if (ev.Status != EventStatus.Completed)
            return Result.Failure<Guid>(Error.Custom("Payroll.EventNotCompleted",
                $"Payroll can only be created after the event is Completed. Current status: {ev.Status}."));

        // 2. AUTO mode — materialise payments for attended crew first.
        var autoCreated = new List<CrewPayment>();
        if ((cmd.PaymentIds is null || cmd.PaymentIds.Count == 0)
            && cmd.DefaultAmountPerCrew is decimal rate && rate > 0)
        {
            // Per the Payment & Settlement Lifecycle in the product doc,
            // attendance is the gate for payment — NOT the assignment-approval
            // workflow. So we accept any non-rejected assignment for this
            // vendor/event that has at least one CheckIn record.
            var rejectedStates = new[]
            {
                AssignmentStatus.Declined,
                AssignmentStatus.RejectedByVendor,
                AssignmentStatus.RejectedByManager,
                AssignmentStatus.NoShow
            };

            var candidateAssignments = await _db.EventAssignments
                .Where(a => a.EventId  == cmd.EventId
                         && a.VendorId == cmd.VendorId
                         && a.CrewId   != null
                         && !rejectedStates.Contains(a.Status))
                .Select(a => new { a.Id, a.CrewId })
                .ToListAsync(ct);

            if (candidateAssignments.Count == 0)
                return Result.Failure<Guid>(Error.Custom("Payroll.NoCrew",
                    "No active crew assignments found for this vendor on this event."));

            var assignmentIds = candidateAssignments.Select(a => a.Id).ToList();

            // Only pay crew that actually checked in at least once.
            var attendedAssignmentIds = await _db.AttendanceRecords
                .Where(r => assignmentIds.Contains(r.AssignmentId)
                         && r.Action == AttendanceAction.CheckIn)
                .Select(r => r.AssignmentId)
                .Distinct()
                .ToListAsync(ct);

            if (attendedAssignmentIds.Count == 0)
                return Result.Failure<Guid>(Error.Custom("Payroll.NoAttendance",
                    "No crew checked in for this event yet — nothing to pay. Mark attendance first."));

            // Skip anyone that already has a payment row.
            var alreadyPaid = await _db.CrewPayments
                .Where(p => attendedAssignmentIds.Contains(p.AssignmentId))
                .Select(p => p.AssignmentId)
                .ToListAsync(ct);

            var toCreate = candidateAssignments
                .Where(a => attendedAssignmentIds.Contains(a.Id)
                         && !alreadyPaid.Contains(a.Id))
                .ToList();

            foreach (var a in toCreate)
            {
                var pmt = new CrewPayment(
                    cmd.EventId, a.Id, a.CrewId!.Value, cmd.VendorId,
                    rate, "Auto-created by payroll batch.");
                pmt.Approve();                       // skip the manual approve step
                _db.CrewPayments.Add(pmt);
                autoCreated.Add(pmt);
            }

            if (autoCreated.Count == 0)
                return Result.Failure<Guid>(Error.Custom("Payroll.AllPaid",
                    "All attended crew already have payments — nothing left to batch."));

            // Persist the new CrewPayments so they have Ids before we attach the batch.
            await _uow.SaveChangesAsync(ct);
        }

        // 3. Resolve the payments that will form this batch.
        List<CrewPayment> payments;
        if (autoCreated.Count > 0)
        {
            payments = autoCreated;
        }
        else
        {
            payments = await _db.CrewPayments
                .Where(p => cmd.PaymentIds.Contains(p.Id)
                         && p.VendorId == cmd.VendorId
                         && p.EventId  == cmd.EventId
                         && p.PayrollBatchId == null)
                .ToListAsync(ct);

            if (payments.Count == 0)
                return Result.Failure<Guid>(Error.Custom("Payroll.NoPayments",
                    "No eligible unbatched payments found."));
        }

        // 4. Create the batch shell and attach the payments.
        var batchRef = $"PAY-{cmd.EventId.ToString()[..8].ToUpper()}-{DateTime.UtcNow:yyyyMMddHHmm}";
        var batch    = new PayrollBatch(cmd.VendorId, cmd.EventId, batchRef, cmd.Notes);

        await _db.PayrollBatches.AddAsync(batch, ct);
        await _uow.SaveChangesAsync(ct);   // get batch.Id

        foreach (var p in payments)
            p.AttachToPayroll(batch.Id);

        var total = payments
            .Where(p => p.Status != PaymentStatus.Rejected)
            .Sum(p => p.AgreedAmount);
        batch.SetTotal(total);
        await _uow.SaveChangesAsync(ct);

        // 5. Live-refresh: tell every affected role the world just changed.
        //    Auto-created payments also fire individual PaymentCreated pushes
        //    so crew see their row appear in /my-payments instantly.
        foreach (var pmt in autoCreated)
        {
            var payload = new
            {
                paymentId = pmt.Id,
                crewId    = pmt.CrewId,
                vendorId  = pmt.VendorId,
                status    = pmt.Status.ToString(),
                action    = "created"
            };
            await _push.PushToUserAsync(pmt.CrewId,   "PaymentCreated", payload, ct);
            if (pmt.VendorId is { } _vid_pmt) await _push.PushToUserAsync(_vid_pmt, "PaymentCreated", payload, ct);
        }
        var batchPayload = new { batchId = batch.Id, status = batch.Status.ToString(), action = "created" };
        await _push.PushToRoleAsync("Admin",   "PayrollUpdated", batchPayload, ct);
        await _push.PushToRoleAsync("Manager", "PayrollUpdated", batchPayload, ct);
        if (cmd.VendorId is { } _vid_cmd) await _push.PushToUserAsync(_vid_cmd, "PayrollUpdated", batchPayload, ct);

        return Result.Success(batch.Id);
    }
}
