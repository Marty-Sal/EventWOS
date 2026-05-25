using System.Security.Claims;
using EventWOS.BlazorWeb.Auth;
using Microsoft.AspNetCore.Components.Authorization;

namespace EventWOS.BlazorWeb.Services;

/// <summary>
/// Provides permission-based access checks in Blazor WASM pages.
/// Reads "permission" claims directly from the current JWT principal.
/// </summary>
public sealed class PermissionGuard
{
    private readonly AuthenticationStateProvider _authState;

    public PermissionGuard(AuthenticationStateProvider authState)
    {
        _authState = authState;
    }

    /// <summary>Returns all permissions the current user holds.</summary>
    public async Task<IReadOnlySet<string>> GetPermissionsAsync()
    {
        var state = await _authState.GetAuthenticationStateAsync();
        var perms = state.User.FindAll(PermissionClaimTypes.Permission)
                               .Select(c => c.Value)
                               .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return perms;
    }

    /// <summary>Returns true if the current user is an Admin (bypasses all permission checks).</summary>
    public async Task<bool> IsAdminAsync()
    {
        var state = await _authState.GetAuthenticationStateAsync();
        return state.User.IsInRole("Admin");
    }

    /// <summary>Returns the current user's role string.</summary>
    public async Task<string?> GetRoleAsync()
    {
        var state = await _authState.GetAuthenticationStateAsync();
        return state.User.FindFirst(ClaimTypes.Role)?.Value;
    }

    /// <summary>
    /// Returns true if the current user can perform the given permission.
    /// Admins always pass. Non-authenticated users always fail.
    /// </summary>
    public async Task<bool> CanAsync(string permission)
    {
        if (await IsAdminAsync()) return true;
        var perms = await GetPermissionsAsync();
        return perms.Contains(permission);
    }

    /// <summary>Returns true if user has ANY of the given permissions (or is Admin).</summary>
    public async Task<bool> CanAnyAsync(params string[] permissions)
    {
        if (await IsAdminAsync()) return true;
        var perms = await GetPermissionsAsync();
        return permissions.Any(p => perms.Contains(p));
    }
}
