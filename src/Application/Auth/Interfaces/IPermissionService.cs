using EventWOS.Domain.Enums;

namespace EventWOS.Application.Auth.Interfaces;

/// <summary>Resolves effective permissions for a user, considering role + overrides.</summary>
public interface IPermissionService
{
    /// <summary>Returns all effective permission names for a user.</summary>
    Task<IReadOnlyList<string>> GetEffectivePermissionsAsync(Guid userId, UserRole role, CancellationToken ct = default);

    /// <summary>
    /// Removes the cached permissions for a user so the next call reads fresh from DB.
    /// Must be called on login to ensure seeded/updated permissions are reflected immediately.
    /// </summary>
    Task InvalidateCacheForUserAsync(Guid userId, CancellationToken ct = default);
}
