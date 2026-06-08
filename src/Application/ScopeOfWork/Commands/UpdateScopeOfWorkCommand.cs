using EventWOS.Application.Interfaces;
using EventWOS.Application.ScopeOfWork.DTOs;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.ScopeOfWork.Commands;

public sealed record UpdateScopeOfWorkCommand(
    Guid    Id,
    string  Name,
    string? Description,
    Guid    ActingUserId
) : IRequest<Result<ScopeOfWorkDto>>;

public sealed class UpdateScopeOfWorkHandler
    : IRequestHandler<UpdateScopeOfWorkCommand, Result<ScopeOfWorkDto>>
{
    private readonly IAppDbContext _db;
    private readonly IUnitOfWork   _uow;
    public UpdateScopeOfWorkHandler(IAppDbContext db, IUnitOfWork uow) { _db = db; _uow = uow; }

    public async Task<Result<ScopeOfWorkDto>> Handle(UpdateScopeOfWorkCommand req, CancellationToken ct)
    {
        var entity = await _db.ScopesOfWork.FirstOrDefaultAsync(s => s.Id == req.Id, ct);
        if (entity is null)
            return Result.Failure<ScopeOfWorkDto>(new Error("ScopeOfWork.NotFound", "Scope of work not found."));

        var newName = (req.Name ?? "").Trim();

        // Duplicate check — exclude the row being edited.
        var dup = await _db.ScopesOfWork
            .Where(s => !s.IsDeleted && s.Id != req.Id && s.Name.ToLower() == newName.ToLower())
            .AnyAsync(ct);
        if (dup)
            return Result.Failure<ScopeOfWorkDto>(new Error(
                "ScopeOfWork.Duplicate",
                $"A scope of work named \"{newName}\" already exists."));

        try
        {
            entity.Update(newName, req.Description);
        }
        catch (ArgumentException ex)
        {
            return Result.Failure<ScopeOfWorkDto>(new Error("ScopeOfWork.Invalid", ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            // Editing an archived row throws — UI should restore first.
            return Result.Failure<ScopeOfWorkDto>(new Error("ScopeOfWork.Archived", ex.Message));
        }

        entity.UpdatedBy = req.ActingUserId;
        await _uow.SaveChangesAsync(ct);

        return Result.Success(new ScopeOfWorkDto(
            entity.Id, entity.Name, entity.Description,
            entity.IsDeleted, entity.CreatedAt, entity.UpdatedAt));
    }
}
