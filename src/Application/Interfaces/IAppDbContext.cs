using EventWOS.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Interfaces;

/// <summary>
/// Abstraction over EF Core DbContext — keeps Application layer decoupled from Persistence.
/// Implemented by AppDbContext in the Persistence layer.
/// </summary>
public interface IAppDbContext
{
    DbSet<User> Users { get; }
    DbSet<Role> Roles { get; }
    DbSet<Permission> Permissions { get; }
    DbSet<RolePermission> RolePermissions { get; }
    DbSet<UserRolePermission> UserRolePermissions { get; }
    DbSet<ManagerPermission> ManagerPermissions { get; }
    DbSet<OtpRequest> OtpRequests { get; }
    DbSet<RefreshToken> RefreshTokens { get; }
    DbSet<UserSession> UserSessions { get; }
    DbSet<VendorCrewMapping> VendorCrewMappings { get; }
    DbSet<AuditLog> AuditLogs { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
