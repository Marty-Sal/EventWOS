using EventWOS.Application.Interfaces;
using EventWOS.Domain.Enums;
using EventWOS.Application.Payments.DTOs;
using EventWOS.Shared.Common;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Payments.Queries;

public sealed record GetPayrollBatchesQuery(
    Guid?  VendorId,
    Guid?  EventId,
    string? Status,
    int    Page     = 1,
    int    PageSize = 20
) : IRequest<Result<PagedResult<PayrollBatchDto>>>;

public sealed class GetPayrollBatchesHandler
    : IRequestHandler<GetPayrollBatchesQuery, Result<PagedResult<PayrollBatchDto>>>
{
    private readonly IAppDbContext _db;
    public GetPayrollBatchesHandler(IAppDbContext db) => _db = db;

    public async Task<Result<PagedResult<PayrollBatchDto>>> Handle(
        GetPayrollBatchesQuery q, CancellationToken ct)
    {
        var query = _db.PayrollBatches.AsQueryable();

        if (q.VendorId.HasValue) query = query.Where(b => b.VendorId == q.VendorId.Value);
        if (q.EventId.HasValue)  query = query.Where(b => b.EventId  == q.EventId.Value);
        if (!string.IsNullOrWhiteSpace(q.Status) &&
            Enum.TryParse<PayrollStatus>(q.Status, true, out var parsedStatus))
            query = query.Where(b => b.Status == parsedStatus);

        var total = await query.CountAsync(ct);

        // Count payments per batch separately to avoid nav-prop translation issues
        var batchIds = await query
            .OrderByDescending(b => b.CreatedAt)
            .Skip((q.Page - 1) * q.PageSize)
            .Take(q.PageSize)
            .Select(b => b.Id)
            .ToListAsync(ct);

        var batches = await query
            .Where(b => batchIds.Contains(b.Id))
            .Select(b => new
            {
                b.Id, b.VendorId, b.EventId, b.BatchRef,
                b.Status, b.TotalAmount, b.Notes,
                b.SubmittedAt, b.ApprovedAt, b.DisbursedAt, b.CreatedAt,
                VendorName = b.Vendor.FullName,
                EventTitle = b.Event.Title
            })
            .ToListAsync(ct);

        var paymentCounts = await _db.CrewPayments
            .Where(p => batchIds.Contains(p.PayrollBatchId!.Value))
            .GroupBy(p => p.PayrollBatchId!.Value)
            .Select(g => new { BatchId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var countLookup = paymentCounts.ToDictionary(x => x.BatchId, x => x.Count);

        var items = batches
            .OrderByDescending(b => b.CreatedAt)
            .Select(b => new PayrollBatchDto(
                b.Id, b.VendorId, b.VendorName,
                b.EventId, b.EventTitle,
                b.BatchRef, b.Status.ToString(), b.TotalAmount, b.Notes,
                countLookup.GetValueOrDefault(b.Id, 0),
                b.SubmittedAt, b.ApprovedAt, b.DisbursedAt, b.CreatedAt))
            .ToList();

        return Result.Success(PagedResult<PayrollBatchDto>.Create(items, total, q.Page, q.PageSize));
    }
}
