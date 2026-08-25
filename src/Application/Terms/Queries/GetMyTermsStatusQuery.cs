using EventOpsOracle.Application.Interfaces;
using EventOpsOracle.Application.Terms.DTOs;
using EventOpsOracle.Domain.Enums;
using EventOpsOracle.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventOpsOracle.Application.Terms.Queries;

/// <summary>
/// Post-login gate check for the CURRENT user: "does your role need to
/// accept a Terms & Conditions document, and if so, have you accepted the
/// latest version?" Only Vendor and Crew roles have a T&C audience —
/// Admin/Manager never see the accept modal.
/// </summary>
public sealed record GetMyTermsStatusQuery(Guid UserId, UserRole Role) : IRequest<Result<TermsStatusDto>>;

public sealed class GetMyTermsStatusHandler : IRequestHandler<GetMyTermsStatusQuery, Result<TermsStatusDto>>
{
    private readonly IAppDbContext _db;
    public GetMyTermsStatusHandler(IAppDbContext db) => _db = db;

    public async Task<Result<TermsStatusDto>> Handle(GetMyTermsStatusQuery req, CancellationToken ct)
    {
        TermsAudience? audience = req.Role switch
        {
            UserRole.Vendor => TermsAudience.Vendor,
            UserRole.Crew   => TermsAudience.Crew,
            _               => null
        };

        if (audience is null)
            return Result.Success(new TermsStatusDto(false, null, null, null));

        var current = await _db.TermsAndConditions
            .Where(t => t.Audience == audience.Value)
            .OrderByDescending(t => t.Version)
            .FirstOrDefaultAsync(ct);

        if (current is null)
            return Result.Success(new TermsStatusDto(false, audience, null, null));

        var accepted = await _db.TermsAcceptances.AnyAsync(a =>
            a.UserId == req.UserId && a.Audience == audience.Value && a.Version == current.Version, ct);

        if (accepted)
            return Result.Success(new TermsStatusDto(false, audience, current.Version, null));

        return Result.Success(new TermsStatusDto(true, audience, current.Version, current.Content));
    }
}
