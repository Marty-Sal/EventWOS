using EventWOS.Application.Interfaces;
using EventWOS.Domain.Entities;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Result;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Payments.Commands;

public sealed record CreatePayrollBatchCommand(
    Guid   VendorId,
    Guid   EventId,
    string? Notes,
    IReadOnlyList<Guid> PaymentIds   // payments to include in this batch
) : IRequest<Result<Guid>>;

public sealed class CreatePayrollBatchValidator : AbstractValidator<CreatePayrollBatchCommand>
{
    public CreatePayrollBatchValidator()
    {
        RuleFor(x => x.VendorId).NotEmpty();
        RuleFor(x => x.EventId).NotEmpty();
        RuleFor(x => x.PaymentIds).NotEmpty().WithMessage("At least one payment is required.");
    }
}

public sealed class CreatePayrollBatchHandler : IRequestHandler<CreatePayrollBatchCommand, Result<Guid>>
{
    private readonly IAppDbContext _db;
    private readonly IUnitOfWork   _uow;

    public CreatePayrollBatchHandler(IAppDbContext db, IUnitOfWork uow)
    {
        _db  = db;
        _uow = uow;
    }

    public async Task<Result<Guid>> Handle(CreatePayrollBatchCommand cmd, CancellationToken ct)
    {
        // Validate payments belong to this vendor/event and are not already batched
        var payments = await _db.CrewPayments
            .Where(p => cmd.PaymentIds.Contains(p.Id)
                     && p.VendorId  == cmd.VendorId
                     && p.EventId   == cmd.EventId
                     && p.PayrollBatchId == null)
            .ToListAsync(ct);

        if (payments.Count == 0)
            return Result.Failure<Guid>(Error.Validation("Payroll.NoPayments",
                "No eligible unbatched payments found."));

        var batchRef = $"PAY-{cmd.EventId.ToString()[..8].ToUpper()}-{DateTime.UtcNow:yyyyMMddHHmm}";
        var batch    = new PayrollBatch(cmd.VendorId, cmd.EventId, batchRef, cmd.Notes);

        await _db.PayrollBatches.AddAsync(batch, ct);
        await _uow.SaveChangesAsync(ct);   // get batch.Id

        foreach (var p in payments)
            p.AttachToPayroll(batch.Id);

        batch.RecalculateTotal();
        await _uow.SaveChangesAsync(ct);

        return Result.Success(batch.Id);
    }
}
