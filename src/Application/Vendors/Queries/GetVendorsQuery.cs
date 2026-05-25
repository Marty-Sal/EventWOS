using EventWOS.Application.Interfaces;
using EventWOS.Application.Vendors.Commands;
using EventWOS.Application.Vendors.DTOs;
using EventWOS.Domain.Enums;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Vendors.Queries;

public sealed record GetVendorsQuery(int Page = 1, int PageSize = 20, string? Search = null)
    : IRequest<Result<PagedVendorResult>>;

public sealed record PagedVendorResult(
    IReadOnlyList<VendorListItemDto> Items, int TotalCount, int Page, int PageSize);

public sealed class GetVendorsHandler : IRequestHandler<GetVendorsQuery, Result<PagedVendorResult>>
{
    private readonly IAppDbContext _db;
    public GetVendorsHandler(IAppDbContext db) => _db = db;

    public async Task<Result<PagedVendorResult>> Handle(GetVendorsQuery req, CancellationToken ct)
    {
        var query = _db.Users.Where(u => u.Role == UserRole.Vendor && !u.IsDeleted);

        if (!string.IsNullOrWhiteSpace(req.Search))
        {
            var pattern = $"%{req.Search.Trim()}%";
            query = query.Where(u =>
                EF.Functions.ILike(u.FullName, pattern) ||
                (u.BusinessName != null && EF.Functions.ILike(u.BusinessName, pattern)) ||
                EF.Functions.ILike(u.Mobile, pattern));
        }

        var total = await query.CountAsync(ct);

        var vendors = await query
            .OrderByDescending(u => u.CreatedAt)
            .Skip((req.Page - 1) * req.PageSize)
            .Take(req.PageSize)
            .ToListAsync(ct);

        // Get crew counts per vendor
        var vendorIds = vendors.Select(v => v.Id).ToList();
        var crewCounts = await _db.Users
            .Where(u => u.Role == UserRole.Crew && u.VendorId != null && vendorIds.Contains(u.VendorId.Value))
            .GroupBy(u => u.VendorId!.Value)
            .Select(g => new { VendorId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.VendorId, x => x.Count, ct);

        var items = vendors.Select(v => new VendorListItemDto(
            v.Id, v.Mobile, v.FullName, v.BusinessName, v.Status.ToString(),
            v.ReferralCode, v.Rating, v.EventsCompleted,
            crewCounts.GetValueOrDefault(v.Id, 0), v.CreatedAt
        )).ToList();

        return Result.Success(new PagedVendorResult(items, total, req.Page, req.PageSize));
    }
}
