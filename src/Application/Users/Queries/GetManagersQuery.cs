using EventWOS.Application.Interfaces;
using EventWOS.Application.Users.DTOs;
using EventWOS.Domain.Enums;
using EventWOS.Shared.Common;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Users.Queries;

public sealed record GetManagersQuery(
    int        PageNumber = 1,
    int        PageSize   = 20,
    string?    Search     = null,
    UserStatus? Status    = null
) : IRequest<Result<PagedResult<ManagerDto>>>;

public sealed class GetManagersHandler
    : IRequestHandler<GetManagersQuery, Result<PagedResult<ManagerDto>>>
{
    private readonly IAppDbContext _db;
    public GetManagersHandler(IAppDbContext db) => _db = db;

    public async Task<Result<PagedResult<ManagerDto>>> Handle(GetManagersQuery req, CancellationToken ct)
    {
        var query = _db.Users.AsNoTracking()
            .Where(u => u.Role == UserRole.Manager && !u.IsDeleted);

        if (!string.IsNullOrWhiteSpace(req.Search))
        {
            var s = req.Search.Trim().ToLower();
            query = query.Where(u =>
                u.Mobile.ToLower().Contains(s) ||
                u.FullName.ToLower().Contains(s) ||
                (u.Email != null && u.Email.ToLower().Contains(s)));
        }

        if (req.Status.HasValue)
            query = query.Where(u => u.Status == req.Status.Value);

        var total = await query.CountAsync(ct);

        var users = await query
            .OrderByDescending(u => u.CreatedAt)
            .Skip((req.PageNumber - 1) * req.PageSize)
            .Take(req.PageSize)
            .ToListAsync(ct);

        var ids = users.Select(u => u.Id).ToList();

        var grants = await _db.ManagerPermissions
            .AsNoTracking()
            .Where(mp => ids.Contains(mp.ManagerId))
            .Include(mp => mp.Permission)
            .ToListAsync(ct);

        var grantsByManager = grants.GroupBy(g => g.ManagerId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var items = users.Select(u =>
        {
            var perms = grantsByManager.TryGetValue(u.Id, out var gs)
                ? gs.Select(g => new ManagerPermissionDto(
                    g.Id, g.PermissionId,
                    g.Permission.Name, g.Permission.Resource,
                    g.Permission.Action, g.Permission.Description,
                    g.IsActive, g.ExpiresAt, g.CreatedAt)).ToList()
                : new List<ManagerPermissionDto>();

            return new ManagerDto(u.Id, u.Mobile, u.FullName, u.Email,
                u.AvatarUrl, u.Status.ToString(),
                u.LastLoginAt, u.CreatedAt, perms);
        }).ToList();

        return Result.Success(PagedResult<ManagerDto>.Create(items, total, req.PageNumber, req.PageSize));
    }
}
