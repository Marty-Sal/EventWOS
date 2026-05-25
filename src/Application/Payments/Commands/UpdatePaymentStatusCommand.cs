using EventWOS.Application.Interfaces;
using EventWOS.Domain.Enums;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Result;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Payments.Commands;

public sealed record UpdatePaymentStatusCommand(
    Guid    PaymentId,
    string  Action,          // "approve" | "pay" | "reject" | "hold"
    decimal? PaidAmount,
    string?  Method,
    string?  TransactionRef,
    string?  Reason
) : IRequest<Result>;

public sealed class UpdatePaymentStatusValidator : AbstractValidator<UpdatePaymentStatusCommand>
{
    public UpdatePaymentStatusValidator()
    {
        RuleFor(x => x.PaymentId).NotEmpty();
        RuleFor(x => x.Action).NotEmpty()
            .Must(a => new[] { "approve","pay","reject","hold" }.Contains(a.ToLower()))
            .WithMessage("Action must be: approve, pay, reject, or hold.");
        When(x => x.Action.ToLower() == "pay", () => {
            RuleFor(x => x.PaidAmount).NotNull().GreaterThan(0);
            RuleFor(x => x.Method).NotEmpty();
        });
    }
}

public sealed class UpdatePaymentStatusHandler : IRequestHandler<UpdatePaymentStatusCommand, Result>
{
    private readonly IAppDbContext _db;
    private readonly IUnitOfWork   _uow;

    public UpdatePaymentStatusHandler(IAppDbContext db, IUnitOfWork uow)
    {
        _db  = db;
        _uow = uow;
    }

    public async Task<Result> Handle(UpdatePaymentStatusCommand cmd, CancellationToken ct)
    {
        var payment = await _db.CrewPayments.FindAsync([cmd.PaymentId], ct);
        if (payment is null)
            return Result.Failure(Error.NotFound("Payment.NotFound", "Payment not found."));

        try
        {
            switch (cmd.Action.ToLower())
            {
                case "approve":
                    payment.Approve();
                    break;

                case "pay":
                    var method = Enum.Parse<PaymentMethod>(cmd.Method!, ignoreCase: true);
                    payment.MarkPaid(cmd.PaidAmount!.Value, method, cmd.TransactionRef);
                    break;

                case "reject":
                    payment.Reject(cmd.Reason ?? "Rejected by admin.");
                    break;

                case "hold":
                    payment.PutOnHold(cmd.Reason ?? "On hold.");
                    break;
            }
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure(Error.Validation("Payment.InvalidTransition", ex.Message));
        }

        await _uow.SaveChangesAsync(ct);
        return Result.Success();
    }
}
