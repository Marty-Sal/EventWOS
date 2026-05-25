using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;

namespace EventWOS.Api.Authorization;

/// <summary>
/// Handles <see cref="PermissionRequirement"/> by checking JWT "permission" claims.
/// Admins always succeed.
/// </summary>
public sealed class PermissionHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        // Admins bypass all permission checks
        if (context.User.IsInRole("Admin"))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        // Check for matching "permission" claim in JWT
        var hasPermission = context.User
            .FindAll("permission")
            .Any(c => string.Equals(c.Value, requirement.Permission, StringComparison.OrdinalIgnoreCase));

        if (hasPermission)
            context.Succeed(requirement);

        return Task.CompletedTask;
    }
}
