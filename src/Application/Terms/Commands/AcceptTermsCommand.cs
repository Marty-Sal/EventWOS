using EventWOS.Application.Interfaces;
using EventWOS.Domain.Entities;
using EventWOS.Domain.Enums;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Terms.Commands;

/// <summary>
/// An existing, authenticated user accepts a Terms & Conditions version —
/// used by the post-login re-accept modal (Admin published a new version
/// since the user last accepted). Registration-time acceptance is written
/// directly by RegisterVendorHandler/RegisterCrewHandler instead, since no
/// authenticated session exists yet at that point.
///
/// Rejects if Version isn't the CURRENT version for Audience — the client
/// always fetches status right before showing the modal, so a mismatch
/// means Admin published yet another version in the interim; the client
/// should refetch and show the newest text rather than recording
/// acceptance of stale text.
/// </summary>
public sealed record AcceptTermsCommand(Guid UserId, TermsAudience Audience, int Version) : IRequest<Result>;

public sealed class AcceptTermsHandler : IRequestHandler<AcceptTermsCommand, Result>
{
    private readonly IAppDbContext _db;
    private readonly IUnitOfWork   _uow;
    public AcceptTermsHandler(IAppDbContext db, IUnitOfWork uow) { _db = db; _uow = uow; }

    public async Task<Result> Handle(AcceptTermsCommand req, CancellationToken ct)
    {
        var current = await _db.TermsAndConditions
            .Where(t => t.Audience == req.Audience)
            .OrderByDescending(t => t.Version)
            .FirstOrDefaultAsync(ct);

        if (current is null || current.Version != req.Version)
            return Result.Failure(new Error(
                "Terms.StaleVersion",
                "This Terms & Conditions text has changed. Please review the latest version and accept again."));

        var alreadyAccepted = await _db.TermsAcceptances.AnyAsync(a =>
            a.UserId == req.UserId && a.Audience == req.Audience && a.Version == req.Version, ct);
        if (alreadyAccepted)
            return Result.Success();

        _db.TermsAcceptances.Add(new TermsAcceptance(req.UserId, req.Audience, req.Version));
        await _uow.SaveChangesAsync(ct);
        return Result.Success();
    }
}
