using EventOpsOracle.Application.Interfaces;
using EventOpsOracle.Application.Venues.Commands;
using EventOpsOracle.Application.Venues.DTOs;
using EventOpsOracle.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventOpsOracle.Application.Venues.Queries;

/// <summary>
/// Single-venue lookup, IgnoreQueryFilters so an archived venue an old
/// event still references can still be resolved (e.g. to preselect the
/// Event-edit form's venue picker even if the venue was later archived).
/// </summary>
public sealed record GetVenueByIdQuery(Guid Id) : IRequest<Result<VenueDto>>;

public sealed class GetVenueByIdHandler : IRequestHandler<GetVenueByIdQuery, Result<VenueDto>>
{
    private readonly IAppDbContext _db;
    public GetVenueByIdHandler(IAppDbContext db) => _db = db;

    public async Task<Result<VenueDto>> Handle(GetVenueByIdQuery req, CancellationToken ct)
    {
        var v = await _db.Venues.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == req.Id, ct);
        if (v is null) return Result.Failure<VenueDto>(Error.NotFound);
        return Result.Success(CreateVenueHandler.ToDto(v));
    }
}
