using EventWOS.Application.Interfaces;
using EventWOS.Application.Venues.Commands;
using EventWOS.Application.Venues.DTOs;
using EventWOS.Shared.Common;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Venues.Queries;

public sealed record GetVenuesQuery(
    string? Search,
    bool    IncludeArchived,
    int     Page,
    int     PageSize,
    // Exact-match state filter for the Event-creation venue picker
    // (state-first, then a searchable venue dropdown within that state).
    string? State = null
) : IRequest<Result<PagedResult<VenueDto>>>;

public sealed class GetVenuesHandler : IRequestHandler<GetVenuesQuery, Result<PagedResult<VenueDto>>>
{
    private readonly IAppDbContext _db;
    public GetVenuesHandler(IAppDbContext db) => _db = db;

    public async Task<Result<PagedResult<VenueDto>>> Handle(GetVenuesQuery req, CancellationToken ct)
    {
        var page     = Math.Max(1, req.Page);
        var pageSize = Math.Clamp(req.PageSize, 1, 200);

        IQueryable<Domain.Entities.Venue> q = _db.Venues;
        if (req.IncludeArchived) q = q.IgnoreQueryFilters();

        if (!string.IsNullOrWhiteSpace(req.State))
        {
            var st = req.State.Trim().ToLower();
            q = q.Where(x => x.State != null && x.State.ToLower() == st);
        }

        if (!string.IsNullOrWhiteSpace(req.Search))
        {
            var s = req.Search.Trim().ToLower();
            q = q.Where(x => x.Name.ToLower().Contains(s)
                          || x.City.ToLower().Contains(s)
                          || (x.State != null && x.State.ToLower().Contains(s)));
        }

        var total = await q.CountAsync(ct);

        var items = await q
            .OrderBy(x => x.IsDeleted)
            .ThenBy(x => x.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var dtos = items.Select(CreateVenueHandler.ToDto).ToList();
        return Result.Success(PagedResult<VenueDto>.Create(dtos, total, page, pageSize));
    }
}
