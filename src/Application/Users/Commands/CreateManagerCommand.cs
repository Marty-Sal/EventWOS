using EventOpsOracle.Application.Interfaces;
using EventOpsOracle.Application.Users.DTOs;
using EventOpsOracle.Domain.Entities;
using EventOpsOracle.Domain.Enums;
using EventOpsOracle.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventOpsOracle.Application.Users.Commands;

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
        var mobile = req.Mobile.Trim();
        var email  = req.Email?.Trim().ToLowerInvariant();

        if (await _db.Users.AnyAsync(u => u.Mobile == mobile && !u.IsDeleted, ct))
            return Result.Failure<ManagerDto>(new Error("Manager.DuplicateMobile", "An account already exists with this mobile number."));
        if (!string.IsNullOrEmpty(email) && await _db.Users.AnyAsync(u => u.Email == email && !u.IsDeleted, ct))
            return Result.Failure<ManagerDto>(new Error("Manager.DuplicateEmail", "An account already exists with this email."));

        var manager = new User(mobile, req.FullName, UserRole.Manager);
        manager.Activate();
        if (email is not null) manager.Email = email;

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
