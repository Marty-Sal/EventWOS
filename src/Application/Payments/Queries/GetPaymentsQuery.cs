using EventWOS.Application.Interfaces;
using EventWOS.Application.Payments.DTOs;
using EventWOS.Shared.Common;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Payments.Queries;

public sealed record GetPaymentsQuery(
    Guid?  EventId,
    Guid?  VendorId,
    Guid?  CrewId,
    string? Status,
    int    Page     = 1,
    int    PageSize = 20
) : IRequest<Result<PagedResult<CrewPaymentDto>>>;

public sealed class GetPaymentsHandler : IRequestHandler<GetPaymentsQuery, Result<PagedResult<CrewPaymentDto>>>
{
    private readonly IAppDbContext _db;
    public GetPaymentsHandler(IAppDbContext db) => _db = db;

    public async Task<Result<PagedResult<CrewPaymentDto>>> Handle(GetPaymentsQuery q, CancellationToken ct)
    {
        var query = _db.CrewPayments.AsQueryable();

        if (q.EventId.HasValue)  query = query.Where(p => p.EventId  == q.EventId.Value);
        if (q.VendorId.HasValue) query = query.Where(p => p.VendorId == q.VendorId.Value);
        if (q.CrewId.HasValue)   query = query.Where(p => p.CrewId   == q.CrewId.Value);
        if (!string.IsNullOrWhiteSpace(q.Status))
            query = query.Where(p => p.Status.ToString() == q.Status);

        var total = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(p => p.CreatedDate)
            .Skip((q.Page - 1) * q.PageSize)
            .Take(q.PageSize)
            .Select(p => new CrewPaymentDto(
                p.Id,
                p.EventId,
                p.Event.Title,
                p.AssignmentId,
                p.CrewId,
                p.Crew.FullName,
                p.Crew.Mobile,
                p.VendorId,
                p.Vendor.FullName,
                p.AgreedAmount,
                p.PaidAmount,
                p.Status.ToString(),
                p.Method.ToString(),
                p.TransactionRef,
                p.Notes,
                p.PaidAt,
                p.PayrollBatchId,
                p.CreatedDate))
            .ToListAsync(ct);

        return Result.Success(PagedResult<CrewPaymentDto>.Create(items, total, q.Page, q.PageSize));
    }
}
