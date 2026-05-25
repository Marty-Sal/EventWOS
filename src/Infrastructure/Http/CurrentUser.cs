using EventWOS.Domain.Enums;
using EventWOS.Domain.Interfaces;
using Microsoft.AspNetCore.Http;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace EventWOS.Infrastructure.Http;

/// <summary>
/// Resolves current user context from HttpContext.User claims.
/// Injected as scoped — fresh per request.
/// </summary>
public sealed class CurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CurrentUser(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    private ClaimsPrincipal? Principal => _httpContextAccessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated ?? false;

    public Guid? UserId
    {
        get
        {
            var sub = Principal?.FindFirstValue(JwtRegisteredClaimNames.Sub)
                   ?? Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(sub, out var id) ? id : null;
        }
    }

    public string? Mobile => Principal?.FindFirstValue("mobile");

    public UserRole? Role
    {
        get
        {
            // "role" claim — check both the raw key and the remapped ClaimTypes.Role URI
            var role = Principal?.FindFirstValue("role")
                    ?? Principal?.FindFirstValue(ClaimTypes.Role);
            return Enum.TryParse<UserRole>(role, out var r) ? r : null;
        }
    }

    public IReadOnlyList<string> Permissions =>
        Principal?.FindAll("permission").Select(c => c.Value).ToList() ?? new List<string>();

    public Guid? SessionId
    {
        get
        {
            var sid = Principal?.FindFirstValue("session_id");
            return Guid.TryParse(sid, out var id) ? id : null;
        }
    }

    public string? DeviceId => Principal?.FindFirstValue("device_id");

    public string? IpAddress =>
        _httpContextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString()
        ?? _httpContextAccessor.HttpContext?.Request.Headers["X-Forwarded-For"].FirstOrDefault();

    public bool IsInRole(UserRole role) => Role == role;

    public bool HasPermission(string permission) =>
        Role == UserRole.Admin || Permissions.Contains(permission);
}
