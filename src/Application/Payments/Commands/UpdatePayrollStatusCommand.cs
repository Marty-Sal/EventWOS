using EventWOS.Application.Interfaces;
using EventWOS.Domain.Interfaces;
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
    private readonly IAppDbContext       _db;
    private readonly IUnitOfWork         _uow;
    private readonly INotificationPusher _push;

    public UpdatePayrollStatusHandler(IAppDbContext db, IUnitOfWork uow, INotificationPusher push)
    {
        _db   = db;
        _uow  = uow;
        _push = push;
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

        await _uow.SaveChangesAsync(ct);

        // Notify Admins and Managers so their /payments view refreshes.
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
