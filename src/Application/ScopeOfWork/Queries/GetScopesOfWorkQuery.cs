using EventWOS.Application.Interfaces;
using EventWOS.Application.ScopeOfWork.DTOs;
using EventWOS.Shared.Common;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.ScopeOfWork.Queries;

/// <summary>
/// List the scope-of-work catalog.
///
/// <paramref name="IncludeArchived"/> controls whether soft-deleted rows
/// are returned. The admin page toggles this; everywhere else (event
/// creation pickers etc.) should pass false so users don't accidentally
/// choose a defunct category.
/// </summary>
public sealed record GetScopesOfWorkQuery(
    string? Search,
    bool    IncludeArchived,
    int     Page,
    int     PageSize
) : IRequest<Result<PagedResult<ScopeOfWorkDto>>>;

public sealed class GetScopesOfWorkHandler
    : IRequestHandler<GetScopesOfWorkQuery, Result<PagedResult<ScopeOfWorkDto>>>
{
    private readonly IAppDbContext _db;
    public GetScopesOfWorkHandler(IAppDbContext db) => _db = db;

    public async Task<Result<PagedResult<ScopeOfWorkDto>>> Handle(GetScopesOfWorkQuery req, CancellationToken ct)
    {
        var page     = Math.Max(1, req.Page);
        var pageSize = Math.Clamp(req.PageSize, 1, 200);

        IQueryable<Domain.Entities.ScopeOfWork> q = _db.ScopesOfWork;
        if (req.IncludeArchived) q = q.IgnoreQueryFilters();

        if (!string.IsNullOrWhiteSpace(req.Search))
        {
            var s = req.Search.Trim().ToLower();
            q = q.Where(x => x.Name.ToLower().Contains(s)
                          || (x.Description != null && x.Description.ToLower().Contains(s)));
        }

        var total = await q.CountAsync(ct);

        // Active rows first, then archived; alphabetic within each group.
        // Same ordering used by the Permissions page so the catalog feels
        // consistent.
        var items = await q
            .OrderBy(x => x.IsDeleted)
            .ThenBy(x => x.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new ScopeOfWorkDto(
                x.Id, x.Name, x.Description, x.IsDeleted, x.CreatedAt, x.UpdatedAt))
            .ToListAsync(ct);

        return Result.Success(PagedResult<ScopeOfWorkDto>.Create(items, total, page, pageSize));
    }
}
