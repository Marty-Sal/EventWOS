using EventOpsOracle.Application.Crew.Commands;
using EventOpsOracle.Application.Interfaces;
using EventOpsOracle.Application.Vendors.DTOs;
using EventOpsOracle.Domain.Enums;
using EventOpsOracle.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventOpsOracle.Application.Crew.Queries;

public sealed record GetCrewQuery(
    int Page = 1, int PageSize = 20,
    string? Search = null, Guid? VendorId = null,
    // Optional exact-match on UserStatus (e.g. "Active", "Rejected") — powers
    // the "Active Crew" / "Rejected Crew" filter chips on the My Crew page.
    // Unrecognized/blank values are ignored, same as an omitted filter.
    string? Status = null
) : IRequest<Result<PagedCrewResult>>;

public sealed record PagedCrewResult(
    IReadOnlyList<CrewDto> Items, int TotalCount, int Page, int PageSize);

public sealed class GetCrewHandler : IRequestHandler<GetCrewQuery, Result<PagedCrewResult>>
{
    private readonly IAppDbContext _db;
    public GetCrewHandler(IAppDbContext db) => _db = db;

    public async Task<Result<PagedCrewResult>> Handle(GetCrewQuery req, CancellationToken ct)
    {
        var query = _db.Users.Where(u => u.Role == UserRole.Crew && !u.IsDeleted);

        if (req.VendorId.HasValue)
            query = query.Where(u => u.VendorId == req.VendorId);

        if (!string.IsNullOrWhiteSpace(req.Status) && Enum.TryParse<UserStatus>(req.Status, ignoreCase: true, out var statusFilter))
            query = query.Where(u => u.Status == statusFilter);

        if (!string.IsNullOrWhiteSpace(req.Search))
        {
            var searchLower = req.Search.Trim().ToLower();
            query = query.Where(u =>
                u.FullName.ToLower().Contains(searchLower) ||
                u.Mobile.ToLower().Contains(searchLower));
        }

        var total = await query.CountAsync(ct);

        var crew = await query
            .OrderByDescending(u => u.CreatedAt)
            .Skip((req.Page - 1) * req.PageSize)
            .Take(req.PageSize)
            .ToListAsync(ct);

        var vendorIds = crew.Where(c => c.VendorId.HasValue).Select(c => c.VendorId!.Value).Distinct().ToList();
        var vendors   = vendorIds.Count > 0
            ? await _db.Users.Where(u => vendorIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct)
            : new Dictionary<Guid, string>();

        var items = crew.Select(c => CreateCrewHandler.MapToDto(
            c, c.VendorId.HasValue ? vendors.GetValueOrDefault(c.VendorId.Value) : null)).ToList();

        return Result.Success(new PagedCrewResult(items, total, req.Page, req.PageSize));
    }
}
