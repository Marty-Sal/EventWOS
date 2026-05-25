using EventWOS.Application.Interfaces;
using EventWOS.Application.Users.DTOs;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Users.Queries;

public sealed record GetAllPermissionsQuery : IRequest<Result<IReadOnlyList<PermissionDto>>>;

public sealed class GetAllPermissionsHandler
    : IRequestHandler<GetAllPermissionsQuery, Result<IReadOnlyList<PermissionDto>>>
{
    private readonly IAppDbContext _db;
    public GetAllPermissionsHandler(IAppDbContext db) => _db = db;

    public async Task<Result<IReadOnlyList<PermissionDto>>> Handle(
        GetAllPermissionsQuery req, CancellationToken ct)
    {
        var perms = await _db.Permissions
            .AsNoTracking()
            .OrderBy(p => p.Resource).ThenBy(p => p.Action)
            .Select(p => new PermissionDto(p.Id, p.Name, p.Resource, p.Action, p.Description))
            .ToListAsync(ct);

        return Result.Success<IReadOnlyList<PermissionDto>>(perms);
    }
}
