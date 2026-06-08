using EventWOS.Application.Interfaces;
using EventWOS.Application.ScopeOfWork.DTOs;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;
using DomainScopeOfWork = EventWOS.Domain.Entities.ScopeOfWork;

namespace EventWOS.Application.ScopeOfWork.Commands;

public sealed record CreateScopeOfWorkCommand(
    string  Name,
    string? Description,
    Guid    ActingUserId
) : IRequest<Result<ScopeOfWorkDto>>;

public sealed class CreateScopeOfWorkHandler
    : IRequestHandler<CreateScopeOfWorkCommand, Result<ScopeOfWorkDto>>
{
    private readonly IAppDbContext _db;
    private readonly IUnitOfWork   _uow;
    public CreateScopeOfWorkHandler(IAppDbContext db, IUnitOfWork uow) { _db = db; _uow = uow; }

    public async Task<Result<ScopeOfWorkDto>> Handle(CreateScopeOfWorkCommand req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return Result.Failure<ScopeOfWorkDto>(new Error("ScopeOfWork.NameRequired", "Name is required."));

        var name = req.Name.Trim();

        // Duplicate check among ACTIVE rows only — case-insensitive. The
        // DB has a filtered unique index that also enforces this, so even
        // if a racing request slips past the .AnyAsync, the insert will
        // fail with a unique-violation. Belt-and-braces.
        var dup = await _db.ScopesOfWork
            .Where(s => !s.IsDeleted && s.Name.ToLower() == name.ToLower())
            .AnyAsync(ct);
        if (dup)
            return Result.Failure<ScopeOfWorkDto>(new Error(
                "ScopeOfWork.Duplicate",
                $"A scope of work named \"{name}\" already exists. " +
                "If it's archived, restore it instead of creating a new one."));

        DomainScopeOfWork entity;
        try
        {
            entity = new DomainScopeOfWork(name, req.Description, req.ActingUserId);
        }
        catch (ArgumentException ex)
        {
            // Domain-side validation failure (length, whitespace, etc.). Surface
            // verbatim so the UI shows the same copy as the unit tests pin.
            return Result.Failure<ScopeOfWorkDto>(new Error("ScopeOfWork.Invalid", ex.Message));
        }

        _db.ScopesOfWork.Add(entity);
        await _uow.SaveChangesAsync(ct);

        return Result.Success(new ScopeOfWorkDto(
            entity.Id, entity.Name, entity.Description,
            entity.IsDeleted, entity.CreatedAt, entity.UpdatedAt));
    }
}
