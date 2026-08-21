using EventWOS.Application.Interfaces;
using EventWOS.Application.Lookups.DTOs;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Lookups.Queries;

/// <summary>
/// The canonical India states + union territories list, for every "State"
/// dropdown in the app (Venue catalog, Vendor/Crew registration, profile
/// editing). Static reference data — no filters, no paging, ~36 rows total.
/// </summary>
public sealed record GetIndianStatesQuery : IRequest<Result<List<IndianStateDto>>>;

public sealed class GetIndianStatesHandler : IRequestHandler<GetIndianStatesQuery, Result<List<IndianStateDto>>>
{
    private readonly IAppDbContext _db;
    public GetIndianStatesHandler(IAppDbContext db) => _db = db;

    public async Task<Result<List<IndianStateDto>>> Handle(GetIndianStatesQuery req, CancellationToken ct)
    {
        var items = await _db.IndianStates
            .OrderBy(s => s.SortOrder)
            .Select(s => new IndianStateDto(s.Name, s.IsUnionTerritory))
            .ToListAsync(ct);

        return Result.Success(items);
    }
}
