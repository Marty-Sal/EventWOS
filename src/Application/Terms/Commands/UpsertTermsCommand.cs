using EventWOS.Application.Interfaces;
using EventWOS.Application.Terms.DTOs;
using EventWOS.Domain.Entities;
using EventWOS.Domain.Enums;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Terms.Commands;

/// <summary>
/// Admin publishes a new Terms & Conditions version for an audience.
/// Always INSERTS a new row (Version = previous max + 1, or 1 if none
/// exist yet) rather than editing in place — see TermsAndConditions'
/// doc comment for why versioning must be append-only.
///
/// Publishing a new version is exactly what flips every existing
/// Vendor/Crew user of that audience to RequiresAcceptance = true on
/// their next login — no separate "notify users" step needed.
/// </summary>
public sealed record UpsertTermsCommand(TermsAudience Audience, string Content, Guid ActingUserId) : IRequest<Result<TermsDto>>;

public sealed class UpsertTermsHandler : IRequestHandler<UpsertTermsCommand, Result<TermsDto>>
{
    private readonly IAppDbContext _db;
    private readonly IUnitOfWork   _uow;
    public UpsertTermsHandler(IAppDbContext db, IUnitOfWork uow) { _db = db; _uow = uow; }

    public async Task<Result<TermsDto>> Handle(UpsertTermsCommand req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Content))
            return Result.Failure<TermsDto>(new Error("Terms.ContentRequired", "Content is required."));

        var maxVersion = await _db.TermsAndConditions
            .Where(t => t.Audience == req.Audience)
            .Select(t => (int?)t.Version)
            .MaxAsync(ct) ?? 0;

        TermsAndConditions entity;
        try
        {
            entity = new TermsAndConditions(req.Audience, maxVersion + 1, req.Content, req.ActingUserId);
        }
        catch (ArgumentException ex)
        {
            return Result.Failure<TermsDto>(new Error("Terms.Invalid", ex.Message));
        }

        _db.TermsAndConditions.Add(entity);
        await _uow.SaveChangesAsync(ct);

        return Result.Success(new TermsDto(
            entity.Id, entity.Audience, entity.Version, entity.Content, entity.CreatedAt, entity.CreatedBy));
    }
}
