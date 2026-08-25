using EventOpsOracle.Application.Interfaces;
using EventOpsOracle.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventOpsOracle.Application.Venues.Queries;

/// <summary>
/// Distinct, non-empty states among active (non-archived) venues — feeds
/// the "pick a state first" step of the Event-creation venue picker so the
/// dropdown only ever shows states that actually have a saved venue.
/// </summary>
public sealed record GetVenueStatesQuery() : IRequest<Result<IReadOnlyList<string>>>;

public sealed class GetVenueStatesHandler : IRequestHandler<GetVenueStatesQuery, Result<IReadOnlyList<string>>>
{
    private readonly IAppDbContext _db;
    public GetVenueStatesHandler(IAppDbContext db) => _db = db;

    public async Task<Result<IReadOnlyList<string>>> Handle(GetVenueStatesQuery req, CancellationToken ct)
    {
        var states = await _db.Venues
            .Where(v => v.State != null && v.State != "")
            .Select(v => v.State!)
            .Distinct()
            .ToListAsync(ct);

        IReadOnlyList<string> sorted = states.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        return Result.Success(sorted);
    }
}
