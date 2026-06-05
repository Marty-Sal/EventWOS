using EventWOS.Application.Interfaces;
using EventWOS.Domain.Entities;
using EventWOS.Domain.Enums;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Result;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Payments.Commands;

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
