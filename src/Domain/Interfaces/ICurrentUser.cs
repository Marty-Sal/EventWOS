using EventWOS.Domain.Enums;

namespace EventWOS.Domain.Interfaces;

/// <summary>Abstraction over the currently authenticated user from HttpContext claims.</summary>
public interface ICurrentUser
{
    Guid? UserId { get; }
    string? Mobile { get; }
    UserRole? Role { get; }
    IReadOnlyList<string> Permissions { get; }
    Guid? SessionId { get; }
    string? DeviceId { get; }
    string? IpAddress { get; }
    bool IsAuthenticated { get; }
    bool IsInRole(UserRole role);
    bool HasPermission(string permission);
}
