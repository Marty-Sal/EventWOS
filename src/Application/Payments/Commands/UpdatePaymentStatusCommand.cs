using EventOpsOracle.Application.Interfaces;
using EventOpsOracle.Application.Notifications.Abstractions;
using EventOpsOracle.Application.Notifications.Contracts;
using EventOpsOracle.Domain.Enums;
using EventOpsOracle.Domain.Interfaces;
using EventOpsOracle.Shared.Result;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventOpsOracle.Application.Payments.Commands;

public sealed record UpdatePaymentStatusCommand(
    Guid    PaymentId,
    string  Action,          // "approve" | "pay" | "reject" | "hold" | "ack-received" | "ack-pending"
    decimal? PaidAmount,
    string?  Method,
    string?  TransactionRef,
    string?  Reason,
    Guid?   ActorId               = null,   // who is calling
    bool    ActorIsAdminOrManager = false   // shortcut so we don't re-check perms here
) : IRequest<Result>;

public sealed class UpdatePaymentStatusValidator : AbstractValidator<UpdatePaymentStatusCommand>
{
    public UpdatePaymentStatusValidator()
    {
        RuleFor(x => x.PaymentId).NotEmpty();
        RuleFor(x => x.Action).NotEmpty()
            .Must(a => new[] { "approve","pay","reject","hold","ack-received","ack-pending" }
                .Contains(a.ToLower()))
            .WithMessage("Action must be: approve, pay, reject, hold, ack-received, or ack-pending.");
        When(x => x.Action.ToLower() == "pay", () => {
            RuleFor(x => x.PaidAmount).NotNull().GreaterThan(0);
            RuleFor(x => x.Method).NotEmpty();
        });
    }
}

public sealed class UpdatePaymentStatusHandler : IRequestHandler<UpdatePaymentStatusCommand, Result>
{
    private readonly IAppDbContext           _db;
    private readonly IUnitOfWork             _uow;
    private readonly INotificationPusher     _push;
    private readonly INotificationDispatcher _notifications;

    public UpdatePaymentStatusHandler(
        IAppDbContext db,
        IUnitOfWork uow,
        INotificationPusher push,
        INotificationDispatcher notifications)
    {
        _db            = db;
        _uow           = uow;
        _push          = push;
        _notifications = notifications;
    }

    public async Task<Result> Handle(UpdatePaymentStatusCommand cmd, CancellationToken ct)
    {
        var payment = await _db.CrewPayments.FindAsync([cmd.PaymentId], ct);
        if (payment is null)
            return Result.Failure(Error.Custom("Payment.NotFound", "Payment not found."));

        // ── Fine-grained ownership rules ─────────────────────────────────────
        var action = cmd.Action.ToLower();
        if (action == "pay" && !cmd.ActorIsAdminOrManager)
        {
            // Vendor disbursement requires the actor BE the payment's vendor.
            if (cmd.ActorId is null || payment.VendorId is null || payment.VendorId.Value != cmd.ActorId.Value)
                return Result.Failure(Error.Custom("Payment.Forbidden",
                    "Only the vendor on this payment can mark it Paid."));
        }
        if (action is "ack-received" or "ack-pending")
        {
            // Crew acknowledgement requires the actor BE the payment's crew.
            if (cmd.ActorId is null || payment.CrewId != cmd.ActorId.Value)
                return Result.Failure(Error.Custom("Payment.Forbidden",
                    "Only the crew member on this payment can acknowledge it."));
        }

        try
        {
            switch (action)
            {
                case "approve":
                    payment.Approve();
                    break;

                case "pay":
                    var method = Enum.Parse<PaymentMethod>(cmd.Method!, ignoreCase: true);

                    // Phase D step 23: AgreedAmount is the contract — Paid MUST equal it.
                    // Two cases:
                    //   1) Standard flow: payment was created via the event-centric batch
                    //      builder with a real AgreedAmount > 0. The vendor (or direct
                    //      payer) only confirms — we ignore whatever PaidAmount the
                    //      client sent and pay exactly AgreedAmount. This guarantees
                    //      the "Paid" column matches the "Agreed" column on every row.
                    //   2) Legacy flow: pre-step-23 batches set AgreedAmount=0 for vendor
                    //      rows; the vendor types the amount at payout. Preserve that
                    //      behaviour for back-compat with existing in-flight batches.
                    decimal payAmount;
                    if (payment.AgreedAmount > 0m)
                    {
                        // Lock to the agreed contract. Client-supplied amount is ignored
                        // even if it tries to drift — admins can change AgreedAmount via
                        // SetAgreedAmount before clicking Pay if they need to adjust.
                        payAmount = payment.AgreedAmount;
                    }
                    else if (payment.VendorId is not null)
                    {
                        // Legacy vendor flow: vendor types the per-crew amount now.
                        // Require the parent batch to be Disbursed first.
                        if (payment.PayrollBatchId is { } pbId)
                        {
                            var batch = await _db.PayrollBatches.FindAsync(new object[] { pbId }, ct);
                            if (batch is null || batch.Status != Domain.Enums.PayrollStatus.Disbursed)
                                return Result.Failure(Error.Custom("Payment.VendorNotPaidYet",
                                    "You can pay crew out only after the manager has disbursed the vendor batch."));
                        }
                        payment.SetAgreedAmountByVendor(cmd.PaidAmount!.Value);
                        payAmount = cmd.PaidAmount!.Value;
                    }
                    else
                    {
                        // Direct-pay row created outside the batch builder, no AgreedAmount yet.
                        // Fall back to whatever the client sent (existing behaviour).
                        payAmount = cmd.PaidAmount!.Value;
                    }

                    payment.MarkPaid(payAmount, method, cmd.TransactionRef);
                    break;

                case "reject":
                    payment.Reject(cmd.Reason ?? "Rejected by admin.");
                    break;

                case "hold":
                    payment.PutOnHold(cmd.Reason ?? "On hold.");
                    break;

                case "ack-received":
                    payment.AcknowledgeReceived(cmd.Reason);
                    break;

                case "ack-pending":
                    payment.AcknowledgePending(cmd.Reason);
                    break;
            }
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure(Error.Custom("Payment.InvalidTransition", ex.Message));
        }

        // Notify the crew member about their OWN money, before the save, so the
        // status change and the notification commit together: a payment that is
        // marked Paid but whose notification was lost is how someone ends up
        // chasing a payment they already received.
        //
        // Only the person-affecting transitions are notified. "hold" is deliberately
        // silent -- it is an internal review state that routinely flips back within
        // minutes, and telling someone their payment is on hold before anyone has
        // looked at it generates a support call, not clarity. The two "ack" actions
        // are the crew's OWN clicks, and notifying you about your own click is spam.
        var notified = action switch
        {
            "approve" => NotificationTemplateCodes.PaymentApproved,
            "pay"     => NotificationTemplateCodes.PayrollReleased,
            "reject"  => NotificationTemplateCodes.PaymentRejected,
            _         => null
        };

        if (notified is not null)
        {
            // Loaded for the wording only, and tolerated as null: a missing event
            // title must not block the money notification. The recipient cares that
            // the amount moved far more than which event it was for.
            var ev = await _db.Events
                .AsNoTracking()
                .Where(e => e.Id == payment.EventId)
                .Select(e => new { e.Title })
                .FirstOrDefaultAsync(ct);

            var crewName = await _db.Users
                .AsNoTracking()
                .Where(u => u.Id == payment.CrewId)
                .Select(u => u.FullName)
                .FirstOrDefaultAsync(ct);

            // The amount actually involved in THIS transition: what was paid for a
            // payout, what was agreed for an approval. Showing the agreed figure in
            // a "released" message when a different sum went out would be a lie the
            // recipient can check against their bank.
            var amount = action == "pay"
                ? payment.PaidAmount ?? payment.AgreedAmount
                : payment.AgreedAmount;

            _notifications.Enqueue(new NotificationRequest(
                notified,
                RecipientUserId: payment.CrewId,
                // Keyed on the payment and the transition: a double-clicked Approve
                // button, or a retried request, resolves to the same key and sends once.
                BusinessEventKey: $"payment:{payment.Id}:{action}",
                Data: new Dictionary<string, string?>
                {
                    [NotificationTokens.RecipientName] = crewName ?? "there",
                    [NotificationTokens.EventName]     = ev?.Title ?? "your event",
                    [NotificationTokens.Amount]        = amount.ToString("N2"),
                    [NotificationTokens.Reason]        = string.IsNullOrWhiteSpace(cmd.Reason)
                        ? "No reason given"
                        : cmd.Reason
                },
                EventId: payment.EventId,
                ActorUserId: cmd.ActorId));
        }

        await _uow.SaveChangesAsync(ct);

        // Real-time fan-out so payment screens refresh without a page reload.
        //
        // KEPT, not replaced. These are cache-invalidation signals, not messages:
        // the role-wide pushes exist so the admin payments table refetches, and
        // MyPayments/VendorPayments reload their rows. Turning them into platform
        // notifications would email an administrator every time a list changed.
        // The platform handles "your payment was approved"; this handles "the table
        // on screen is now stale".
        var evt = cmd.Action.ToLower() switch
        {
            "approve"      => "PaymentApproved",
            "pay"          => "PaymentPaid",
            "reject"       => "PaymentRejected",
            "hold"         => "PaymentOnHold",
            "ack-received" => "PaymentAcknowledged",
            "ack-pending"  => "PaymentAcknowledged",
            _              => "PaymentUpdated"
        };
        var payload = new
        {
            paymentId = payment.Id,
            crewId    = payment.CrewId,
            vendorId  = payment.VendorId,
            status    = payment.Status.ToString(),
            action    = cmd.Action.ToLower()
        };
        // Crew owner sees update on /my-payments
        await _push.PushToUserAsync(payment.CrewId,   evt, payload, ct);
        // Vendor sees update on /vendor-payments
        if (payment.VendorId is { } _vid_evt) await _push.PushToUserAsync(_vid_evt, evt, payload, ct);
        // Admins/Managers see the master /payments list refresh
        await _push.PushToRoleAsync("Admin",   evt, payload, ct);
        await _push.PushToRoleAsync("Manager", evt, payload, ct);

        return Result.Success();
    }
}
