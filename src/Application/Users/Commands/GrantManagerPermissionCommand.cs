using EventWOS.Application.Interfaces;
using EventWOS.Application.Users.DTOs;
using EventWOS.Domain.Entities;
using EventWOS.Domain.Enums;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Users.Commands;

public sealed record GrantManagerPermissionCommand(
    Guid      ManagerId,
    Guid      PermissionId,
    Guid      GrantedByAdminId,
    DateTime? ExpiresAt
) : IRequest<Result<ManagerPermissionDto>>;

public sealed class GrantManagerPermissionHandler
    : IRequestHandler<GrantManagerPermissionCommand, Result<ManagerPermissionDto>>
{
    private readonly IAppDbContext _db;
    public GrantManagerPermissionHandler(IAppDbContext db) => _db = db;

    public async Task<Result<ManagerPermissionDto>> Handle(
        GrantManagerPermissionCommand req, CancellationToken ct)
    {
        var manager = await _db.Users.FirstOrDefaultAsync(
            u => u.Id == req.ManagerId && u.Role == UserRole.Manager && !u.IsDeleted, ct);
        if (manager is null)
            return Result.Failure<ManagerPermissionDto>(
                new Error("Manager.NotFound", "Manager not found."));

        var permission = await _db.Permissions.FirstOrDefaultAsync(
            p => p.Id == req.PermissionId, ct);
        if (permission is null)
            return Result.Failure<ManagerPermissionDto>(
                new Error("Permission.NotFound", "Permission not found."));

        var existing = await _db.ManagerPermissions.FirstOrDefaultAsync(
            mp => mp.ManagerId == req.ManagerId && mp.PermissionId == req.PermissionId, ct);

        if (existing is not null)
        {
            if (existing.IsActive)
                return Result.Failure<ManagerPermissionDto>(
                    new Error("Manager.PermissionAlreadyGranted", "Permission already active."));
            existing.Reactivate();
            if (req.ExpiresAt.HasValue) existing.SetExpiry(req.ExpiresAt.Value);
            await _db.SaveChangesAsync(ct);
            return Result.Success(MapToDto(existing, permission));
        }

        var grant = new ManagerPermission(req.ManagerId, req.PermissionId, req.GrantedByAdminId);
        if (req.ExpiresAt.HasValue) grant.SetExpiry(req.ExpiresAt.Value);

        _db.ManagerPermissions.Add(grant);
        await _db.SaveChangesAsync(ct);
        return Result.Success(MapToDto(grant, permission));
    }

    private static ManagerPermissionDto MapToDto(ManagerPermission mp, Permission p) =>
        new(mp.Id, p.Id, p.Name, p.Resource, p.Action, p.Description,
            mp.IsActive, mp.ExpiresAt, mp.CreatedAt);
}
