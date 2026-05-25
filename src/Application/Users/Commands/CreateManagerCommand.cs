using EventWOS.Application.Interfaces;
using EventWOS.Application.Users.DTOs;
using EventWOS.Domain.Entities;
using EventWOS.Domain.Enums;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Users.Commands;

public sealed record CreateManagerCommand(
    string  Mobile,
    string  FullName,
    string? Email,
    Guid    CreatedByAdminId
) : IRequest<Result<ManagerDto>>;

public sealed class CreateManagerHandler : IRequestHandler<CreateManagerCommand, Result<ManagerDto>>
{
    private readonly IAppDbContext _db;
    public CreateManagerHandler(IAppDbContext db) => _db = db;

    public async Task<Result<ManagerDto>> Handle(CreateManagerCommand req, CancellationToken ct)
    {
        if (await _db.Users.AnyAsync(u => u.Mobile == req.Mobile && !u.IsDeleted, ct))
            return Result.Failure<ManagerDto>(new Error("Manager.DuplicateMobile", "Mobile already registered."));

        var manager = new User(req.Mobile, req.FullName, UserRole.Manager);
        manager.Activate();
        if (req.Email is not null) manager.Email = req.Email;

        _db.Users.Add(manager);
        await _db.SaveChangesAsync(ct);

        return Result.Success(new ManagerDto(
            manager.Id, manager.Mobile, manager.FullName,
            manager.Email, manager.AvatarUrl,
            manager.Status.ToString(),
            manager.LastLoginAt, manager.CreatedAt,
            new List<ManagerPermissionDto>()));
    }
}
