using EventWOS.Application.Interfaces;
using EventWOS.Domain.Entities;
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
    Guid    VendorId,
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
        RuleFor(x => x.VendorId).NotEmpty();
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
        await _uow.SaveChangesAsync(ct);

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
        await _push.PushToUserAsync(payment.VendorId, "PaymentCreated", payload, ct);
        await _push.PushToRoleAsync("Admin",          "PaymentCreated", payload, ct);
        await _push.PushToRoleAsync("Manager",        "PaymentCreated", payload, ct);

        return Result.Success(payment.Id);
    }
}
