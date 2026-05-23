using EventWOS.Domain.Enums;

namespace EventWOS.Application.Auth.Interfaces;

/// <summary>Resolves effective permissions for a user, considering role + overrides.</summary>
public interface IPermissionService
{
    /// <summary>Returns all effective permission names for a user.</summary>
    Task<IReadOnlyList<string>> GetEffectivePermissionsAsync(Guid userId, UserRole role, CancellationToken ct = default);
}
