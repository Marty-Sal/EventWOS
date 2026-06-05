using EventWOS.Application.CrewGroups.DTOs;
using EventWOS.Application.Interfaces;
using EventWOS.Shared.Common;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.CrewGroups.Queries;

/// <summary>List crew groups owned by a given vendor.</summary>
public sealed record GetCrewGroupsQuery(
    Guid VendorId,
    int  Page     = 1,
    int  PageSize = 50,
    string? Search = null
) : IRequest<Result<PagedResult<CrewGroupDto>>>;

public sealed class GetCrewGroupsHandler
    : IRequestHandler<GetCrewGroupsQuery, Result<PagedResult<CrewGroupDto>>>
{
    private readonly IAppDbContext _db;
    public GetCrewGroupsHandler(IAppDbContext db) => _db = db;

    public async Task<Result<PagedResult<CrewGroupDto>>> Handle(GetCrewGroupsQuery req, CancellationToken ct)
    {
        var q = _db.CrewGroups.AsNoTracking().Where(g => g.VendorId == req.VendorId);

        if (!string.IsNullOrWhiteSpace(req.Search))
        {
            var s = req.Search.Trim().ToLower();
            q = q.Where(g => g.Name.ToLower().Contains(s));
        }

        var total = await q.CountAsync(ct);

        // Project with live member count (only non-deleted members thanks to global filter).
        var page = await q
            .OrderByDescending(g => g.CreatedAt)
            .Skip((req.Page - 1) * req.PageSize)
            .Take(req.PageSize)
            .Select(g => new CrewGroupDto(
                g.Id,
                g.VendorId,
                g.Name,
                g.Description,
                _db.CrewGroupMembers.Count(m => m.CrewGroupId == g.Id),
                g.CreatedAt))
            .ToListAsync(ct);

        return Result.Success(PagedResult<CrewGroupDto>.Create(page, total, req.Page, req.PageSize));
    }
}
