using EventWOS.Application.Interfaces;
using EventWOS.Application.Notifications.Abstractions;
using EventWOS.Application.Notifications.Contracts;
using EventWOS.Domain.Interfaces;
using Microsoft.EntityFrameworkCore;
using EventWOS.Shared.Result;
using MediatR;

namespace EventWOS.Application.Payments.Commands;

public sealed record UpdatePayrollStatusCommand(
    Guid   BatchId,
    string Action,   // "submit" | "approve" | "disburse" | "reject"
    Guid   ActorId,
    string? Reason
) : IRequest<Result>;

public sealed class UpdatePayrollStatusHandler : IRequestHandler<UpdatePayrollStatusCommand, Result>
{
    private readonly IAppDbContext           _db;
    private readonly IUnitOfWork             _uow;
    private readonly INotificationPusher     _push;
    private readonly INotificationDispatcher _notifications;

    public UpdatePayrollStatusHandler(
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

    public async Task<Result> Handle(UpdatePayrollStatusCommand cmd, CancellationToken ct)
    {
        var batch = await _db.PayrollBatches.FindAsync([cmd.BatchId], ct);
        if (batch is null)
            return Result.Failure(Error.Custom("Payroll.NotFound", "Payroll batch not found."));

        try
        {
            switch (cmd.Action.ToLower())
            {
                case "submit":   batch.Submit();              break;
                case "approve":  batch.Approve(cmd.ActorId); break;
                case "disburse": batch.Disburse();            break;
                case "reject":   batch.Reject(cmd.Reason ?? "Rejected."); break;
                default:
                    return Result.Failure(Error.Custom("Payroll.InvalidAction",
                        "Action must be: submit, approve, disburse, or reject."));
            }
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure(Error.Custom("Payroll.InvalidTransition", ex.Message));
        }

        // Disbursement is the only transition anyone outside the finance team needs
        // to hear about: it is the moment money leaves. submit/approve/reject are
        // internal workflow steps between admins and managers, who are looking at
        // the payments screen anyway -- notifying a vendor that their batch was
        // "submitted" tells them nothing they can act on.
        if (cmd.Action.Equals("disburse", StringComparison.OrdinalIgnoreCase) && batch.VendorId is { } vendorId)
        {
            var ev = await _db.Events
                .AsNoTracking()
                .Where(e => e.Id == batch.EventId)
                .Select(e => new { e.Title })
                .FirstOrDefaultAsync(ct);

            var vendorName = await _db.Users
                .AsNoTracking()
                .Where(u => u.Id == vendorId)
                .Select(u => u.FullName)
                .FirstOrDefaultAsync(ct);

            _notifications.Enqueue(new NotificationRequest(
                NotificationTemplateCodes.PayrollReleased,
                RecipientUserId: vendorId,
                BusinessEventKey: $"payroll:{batch.Id}:disbursed",
                Data: new Dictionary<string, string?>
                {
                    [NotificationTokens.RecipientName] = vendorName ?? "there",
                    [NotificationTokens.EventName]     = ev?.Title ?? "your event",
                    [NotificationTokens.Amount]        = batch.TotalAmount.ToString("N2")
                },
                EventId: batch.EventId,
                ActorUserId: cmd.ActorId));

            // The vendor's crew are NOT notified here. Their individual payments are
            // still Pending at this point -- disbursement funds the vendor, who then
            // pays each crew member out, and that per-crew payout raises its own
            // PAYROLL_RELEASED via UpdatePaymentStatusCommand. Announcing money to
            // crew now would have them asking where it is for days.
        }

        await _uow.SaveChangesAsync(ct);

        // Notify Admins and Managers so their /payments view refreshes.
        // KEPT: a table-refresh signal, not a message. See UpdatePaymentStatusCommand.
        var payload = new
        {
            batchId = batch.Id,
            status  = batch.Status.ToString(),
            action  = cmd.Action.ToLower()
        };
        await _push.PushToRoleAsync("Admin",   "PayrollUpdated", payload, ct);
        await _push.PushToRoleAsync("Manager", "PayrollUpdated", payload, ct);

        return Result.Success();
    }
}
