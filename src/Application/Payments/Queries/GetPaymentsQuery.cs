using EventWOS.Application.Interfaces;
using EventWOS.Domain.Enums;
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
        if (!string.IsNullOrWhiteSpace(q.Status) &&
            Enum.TryParse<PaymentStatus>(q.Status, true, out var parsedStatus))
            query = query.Where(p => p.Status == parsedStatus);

        var total = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(p => p.CreatedAt)
            .Skip((q.Page - 1) * q.PageSize)
            .Take(q.PageSize)
            // Phase D step 28: project shift via Assignment.ShiftId subquery.
            // Same pattern as GetMyAssignmentsQuery / GetAttendanceListQuery —
            // EventAssignment has no Shift nav property, so we hop through
            // _db.EventShifts manually. Translates to a LEFT JOIN under EF.
            .Select(p => new CrewPaymentDto(
                p.Id,
                p.EventId,
                p.Event.Title,
                p.AssignmentId,
                p.CrewId,
                p.Crew.FullName,
                p.Crew.Mobile,
                p.VendorId,
                p.Vendor == null ? null : p.Vendor.FullName,
                p.AgreedAmount,
                p.PaidAmount,
                p.Status.ToString(),
                (p.Method == null ? null : p.Method.ToString()),
                p.TransactionRef,
                p.Notes,
                p.PaidAt,
                p.PayrollBatchId,
                p.CrewAcknowledgment.ToString(),
                p.AcknowledgedAt,
                p.AcknowledgmentNote,
                p.PayrollBatch == null ? null : p.PayrollBatch.Status.ToString(),
                p.PayrollBatch == null ? null : (decimal?)p.PayrollBatch.TotalAmount,
                p.CreatedAt,
                _db.EventAssignments
                    .Where(a => a.Id == p.AssignmentId)
                    .Select(a => a.ShiftId)
                    .Where(sid => sid.HasValue)
                    .SelectMany(sid => _db.EventShifts
                        .Where(s => s.Id == sid!.Value)
                        .Select(s => (string?)s.ScopeOfWork.Name))
                    .FirstOrDefault(),
                _db.EventAssignments
                    .Where(a => a.Id == p.AssignmentId)
                    .Select(a => a.ShiftId)
                    .Where(sid => sid.HasValue)
                    .SelectMany(sid => _db.EventShifts
                        .Where(s => s.Id == sid!.Value)
                        .Select(s => (DateTime?)s.StartAt))
                    .FirstOrDefault(),
                _db.EventAssignments
                    .Where(a => a.Id == p.AssignmentId)
                    .Select(a => a.ShiftId)
                    .Where(sid => sid.HasValue)
                    .SelectMany(sid => _db.EventShifts
                        .Where(s => s.Id == sid!.Value)
                        .Select(s => s.EndAt))
                    .FirstOrDefault()))
            .ToListAsync(ct);

        return Result.Success(PagedResult<CrewPaymentDto>.Create(items, total, q.Page, q.PageSize));
    }
}
