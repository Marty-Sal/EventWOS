using Microsoft.AspNetCore.Authorization;

namespace EventWOS.Api.Authorization;

/// <summary>
/// Authorization requirement that checks the "permission" claim in the JWT.
/// Admins bypass this check automatically.
/// </summary>
public sealed class PermissionRequirement : IAuthorizationRequirement
{
    public string Permission { get; }
    public PermissionRequirement(string permission) => Permission = permission;
}
