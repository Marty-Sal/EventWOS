using EventOpsOracle.Application.Interfaces;
using EventOpsOracle.Domain.Entities;
using EventOpsOracle.Domain.Enums;
using EventOpsOracle.Domain.Interfaces;
using EventOpsOracle.Shared.Result;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventOpsOracle.Application.Payments.Commands;

public sealed record CreateCrewPaymentCommand(
    Guid    EventId,
    Guid    AssignmentId,
    Guid    CrewId,
    Guid?   VendorId,                // null = direct-crew payment (no vendor)
    decimal AgreedAmount,
    string? Notes
) : IRequest<Result<Guid>>;

public sealed class CreateCrewPaymentValidator : AbstractValidator<CreateCrewPaymentCommand>
{
    public CreateCrewPaymentValidator()
    {
        RuleFor(x => x.EventId).NotEmpty();
        RuleFor(x => x.AssignmentId).NotEmpty();
        RuleFor(x => x.CrewId).NotEmpty();

        RuleFor(x => x.AgreedAmount).GreaterThan(0).WithMessage("Amount must be greater than 0.");
    }
}

public sealed class CreateCrewPaymentHandler : IRequestHandler<CreateCrewPaymentCommand, Result<Guid>>
{
    private readonly IAppDbContext       _db;
    private readonly IUnitOfWork         _uow;
    private readonly INotificationPusher _push;

    public CreateCrewPaymentHandler(IAppDbContext db, IUnitOfWork uow, INotificationPusher push)
    {
        _db   = db;
        _uow  = uow;
        _push = push;
    }

    public async Task<Result<Guid>> Handle(CreateCrewPaymentCommand cmd, CancellationToken ct)
    {
        // Payments are only valid once the event has wrapped up. Block here so
        // the rule is enforced regardless of how the request reached the API.
        var ev = await _db.Events.FindAsync([cmd.EventId], ct);
        if (ev is null)
            return Result.Failure<Guid>(Error.Custom("Payment.EventNotFound", "Event not found."));
        if (ev.Status != EventStatus.Completed)
            return Result.Failure<Guid>(Error.Custom("Payment.EventNotCompleted",
                $"Payments can only be created after the event is Completed. Current status: {ev.Status}."));

        // Prevent duplicate payment for same assignment
        var exists = await _db.CrewPayments
            .AnyAsync(p => p.AssignmentId == cmd.AssignmentId, ct);

        if (exists)
            return Result.Failure<Guid>(Error.Custom("Payment.Duplicate",
                "A payment already exists for this assignment."));

        var payment = new CrewPayment(
            cmd.EventId, cmd.AssignmentId, cmd.CrewId, cmd.VendorId,
            cmd.AgreedAmount, cmd.Notes);

        await _db.CrewPayments.AddAsync(payment, ct);

        // ── Vendor-routed direct payments: auto-wrap in a PayrollBatch ────────
        // When a crew was invited via a vendor (VendorId is set), the manager
        // can still create an ad-hoc payment from the "+ New Payment" form.
        // Historically that left the row orphaned (no batch), so the vendor
        // never got the standard Approve → Disburse → MarkPaid flow. We now
        // attach it to an existing Draft batch for the same vendor+event, or
        // spin up a fresh one. Direct-to-crew payments (no vendor) still
        // skip this — those are paid out by the organiser directly.
        PayrollBatch? autoBatch = null;
        bool          autoBatchIsNew = false;
        if (cmd.VendorId is { } _vidAuto)
        {
            autoBatch = await _db.PayrollBatches
                .Where(b => b.VendorId == _vidAuto
                         && b.EventId  == cmd.EventId
                         && b.Status   == PayrollStatus.Draft)
                .OrderByDescending(b => b.CreatedAt)
                .FirstOrDefaultAsync(ct);

            if (autoBatch is null)
            {
                var batchRef = $"PAY-{cmd.EventId.ToString()[..8].ToUpper()}-{DateTime.UtcNow:yyyyMMddHHmm}";
                autoBatch = new PayrollBatch(_vidAuto, cmd.EventId, batchRef, cmd.Notes);
                await _db.PayrollBatches.AddAsync(autoBatch, ct);
                autoBatchIsNew = true;
            }
        }

        await _uow.SaveChangesAsync(ct);   // get payment.Id + autoBatch.Id

        if (autoBatch is not null)
        {
            payment.AttachToPayroll(autoBatch.Id);

            // Recalculate batch total from all non-rejected payments now attached.
            var batchTotal = await _db.CrewPayments
                .Where(p => p.PayrollBatchId == autoBatch.Id
                         && p.Status != PaymentStatus.Rejected)
                .SumAsync(p => p.AgreedAmount, ct)
                + payment.AgreedAmount; // include the row we just attached (not yet flushed)
            autoBatch.SetTotal(batchTotal);

            await _uow.SaveChangesAsync(ct);
        }

        // ── Deliberately no durable notification on creation ────────────────────
        // A fresh payment is Pending and a fresh batch is Draft: no money has moved,
        // no decision has been taken, and the row can still be rejected. The moments
        // that actually concern the crew member are already on the notification
        // platform in UpdatePaymentStatusCommand / UpdatePayrollStatusCommand --
        // PAYMENT_APPROVED, PAYROLL_RELEASED, PAYMENT_REJECTED -- each carrying the
        // amount involved in that specific transition.
        //
        // "A payment has been recorded, pending approval" would instead put a WhatsApp
        // message in front of every crew member on a batch (hundreds on a big event)
        // about money nobody has approved yet, and any message about a payment reads
        // as "I have been paid" to someone skimming. The channel stays worth reading
        // only if it is spent on facts the recipient can act on.
        //
        // The pushes below are NOT user-facing news: they are cache-invalidation
        // signals that make MyPayments / Payments / VendorPayments refetch, and no
        // toast subscribes to them. Keep them.
        // NO platform notification here, deliberately. A payment is created as
        // Pending -- an internal draft the finance team still has to approve -- so
        // telling a crew member "a payment of 4,500 exists" would announce money
        // that nobody has agreed to release yet, and they would start chasing it.
        // The crew member hears about it at approval and at payout, both of which
        // dispatch from UpdatePaymentStatusCommand. Please do not "fix" this by
        // adding a PAYMENT_CREATED notification.
        //
        // The pushes below stay: they are table-refresh signals for screens that
        // are already open, not messages addressed to a person.
        // Fan out so each role's payment screen surfaces the new row live.
        var payload = new
        {
            paymentId = payment.Id,
            crewId    = payment.CrewId,
            vendorId  = payment.VendorId,
            status    = payment.Status.ToString(),
            action    = "created"
        };
        await _push.PushToUserAsync(payment.CrewId,   "PaymentCreated", payload, ct);

        if (payment.VendorId is { } _vid_pc) await _push.PushToUserAsync(_vid_pc, "PaymentCreated", payload, ct);
        await _push.PushToRoleAsync("Admin",          "PaymentCreated", payload, ct);
        await _push.PushToRoleAsync("Manager",        "PaymentCreated", payload, ct);

        // Tell every payments screen the batch moved so the row regroups.
        if (autoBatch is not null)
        {
            var batchPayload = new
            {
                batchId = autoBatch.Id,
                status  = autoBatch.Status.ToString(),
                action  = autoBatchIsNew ? "created" : "updated"
            };
            await _push.PushToRoleAsync("Admin",   "PayrollUpdated", batchPayload, ct);
            await _push.PushToRoleAsync("Manager", "PayrollUpdated", batchPayload, ct);
            if (payment.VendorId is { } _vid_bp)
                await _push.PushToUserAsync(_vid_bp, "PayrollUpdated", batchPayload, ct);
        }

        return Result.Success(payment.Id);
    }
}
