using Microsoft.AspNetCore.Authorization;

namespace EventWOS.Api.Authorization;

/// <summary>
/// Shorthand attribute to require a specific "permission" JWT claim on a controller or action.
/// Usage: [Permission("payments:read")]
/// </summary>
public sealed class PermissionAttribute : AuthorizeAttribute
{
    public PermissionAttribute(string permission)
        : base(policy: $"perm:{permission}") { }
}
