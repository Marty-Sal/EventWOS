using EventWOS.Application.Auth.Interfaces;
using EventWOS.Domain.Enums;
using EventWOS.Application.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using System.Text.Json;

namespace EventWOS.Infrastructure.Auth;

/// <summary>
/// Resolves effective permissions by combining:
/// 1. Role-level permissions (from RolePermissions table)
/// 2. User-level overrides (from UserRolePermissions — can grant OR deny)
/// 3. For Managers: ManagerPermissions take precedence
/// Result is cached in Redis per user for 5 minutes.
/// </summary>
public sealed class PermissionService : IPermissionService
{
    private readonly IAppDbContext _db;
    private readonly IDistributedCache _cache;

    public PermissionService(IAppDbContext db, IDistributedCache cache)
    {
        _db = db;
        _cache = cache;
    }

    public async Task<IReadOnlyList<string>> GetEffectivePermissionsAsync(
        Guid userId, UserRole role, CancellationToken ct = default)
    {
        // Admins always get all permissions — no DB round trip
        if (role == UserRole.Admin)
            return await GetAllPermissionNamesAsync(ct);

        var cacheKey = $"perms:{userId}";
        var cached = await _cache.GetStringAsync(cacheKey, ct);
        if (cached is not null)
            return JsonSerializer.Deserialize<List<string>>(cached)!;

        List<string> permissions;

        if (role == UserRole.Manager)
        {
            // Managers only have explicitly granted permissions
            permissions = await _db.ManagerPermissions
                .Where(mp => mp.ManagerId == userId && mp.IsActive &&
                             (mp.ExpiresAt == null || mp.ExpiresAt > DateTime.UtcNow))
                .Include(mp => mp.Permission)
                .Select(mp => mp.Permission.Name)
                .ToListAsync(ct);
        }
        else
        {
            // Vendor / Crew: start with role permissions
            var rolePerms = await _db.RolePermissions
                .Where(rp => rp.Role.RoleType == role && rp.IsGranted)
                .Include(rp => rp.Permission)
                .Select(rp => rp.Permission.Name)
                .ToListAsync(ct);

            // Apply user-level overrides
            var overrides = await _db.UserRolePermissions
                .Where(up => up.UserId == userId &&
                             (up.ExpiresAt == null || up.ExpiresAt > DateTime.UtcNow))
                .Include(up => up.Permission)
                .Select(up => new { up.Permission.Name, up.IsGranted })
                .ToListAsync(ct);

            var permSet = new HashSet<string>(rolePerms);
            foreach (var o in overrides)
            {
                if (o.IsGranted) permSet.Add(o.Name);
                else permSet.Remove(o.Name);
            }

            permissions = permSet.ToList();
        }

        // Cache for 5 minutes
        await _cache.SetStringAsync(cacheKey,
            JsonSerializer.Serialize(permissions),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) },
            ct);

        return permissions;
    }

    public async Task InvalidateCacheForUserAsync(Guid userId, CancellationToken ct = default)
    {
        var cacheKey = $"perms:{userId}";
        await _cache.RemoveAsync(cacheKey, ct);
    }

    private async Task<IReadOnlyList<string>> GetAllPermissionNamesAsync(CancellationToken ct)
    {
        const string key = "perms:all";
        var cached = await _cache.GetStringAsync(key, ct);
        if (cached is not null)
            return JsonSerializer.Deserialize<List<string>>(cached)!;

        var all = await _db.Permissions.Select(p => p.Name).ToListAsync(ct);
        await _cache.SetStringAsync(key,
            JsonSerializer.Serialize(all),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30) },
            ct);
        return all;
    }
}
