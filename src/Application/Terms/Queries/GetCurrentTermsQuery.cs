using EventOpsOracle.Application.Interfaces;
using EventOpsOracle.Application.Terms.DTOs;
using EventOpsOracle.Domain.Enums;
using EventOpsOracle.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventOpsOracle.Application.Terms.Queries;

/// <summary>
/// The current (highest-version) Terms & Conditions document for an
/// audience. AllowAnonymous at the API layer — Vendor/Crew self-
/// registration pages need this BEFORE the user has an account, so they
/// can show the text and require the accept checkbox pre-submit.
///
/// Returns null (not a failure) when Admin hasn't published anything yet
/// for that audience — callers treat "no terms configured" as "nothing to
/// accept" rather than blocking registration/login on a feature the
/// admin hasn't set up.
/// </summary>
public sealed record GetCurrentTermsQuery(TermsAudience Audience) : IRequest<Result<TermsDto?>>;

public sealed class GetCurrentTermsHandler : IRequestHandler<GetCurrentTermsQuery, Result<TermsDto?>>
{
    private readonly IAppDbContext _db;
    public GetCurrentTermsHandler(IAppDbContext db) => _db = db;

    public async Task<Result<TermsDto?>> Handle(GetCurrentTermsQuery req, CancellationToken ct)
    {
        var current = await _db.TermsAndConditions
            .Where(t => t.Audience == req.Audience)
            .OrderByDescending(t => t.Version)
            .FirstOrDefaultAsync(ct);

        if (current is null) return Result.Success<TermsDto?>(null);

        return Result.Success<TermsDto?>(new TermsDto(
            current.Id, current.Audience, current.Version, current.Content, current.CreatedAt, current.CreatedBy));
    }
}
