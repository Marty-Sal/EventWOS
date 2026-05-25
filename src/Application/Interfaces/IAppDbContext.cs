using EventWOS.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Interfaces;

public interface IAppDbContext
{
    DbSet<User>             Users             { get; }
    DbSet<Role>             Roles             { get; }
    DbSet<Permission>       Permissions       { get; }
    DbSet<RolePermission>   RolePermissions   { get; }
    DbSet<UserRolePermission> UserRolePermissions { get; }
    DbSet<ManagerPermission>  ManagerPermissions  { get; }
    DbSet<OtpRequest>       OtpRequests       { get; }
    DbSet<RefreshToken>     RefreshTokens     { get; }
    DbSet<UserSession>      UserSessions      { get; }
    DbSet<VendorCrewMapping> VendorCrewMappings { get; }
    DbSet<AuditLog>         AuditLogs         { get; }

    // Phase 2 — Events Module
    DbSet<Event>            Events            { get; }
    DbSet<EventAssignment>  EventAssignments  { get; }
    DbSet<AttendanceRecord> AttendanceRecords { get; }
    DbSet<CrewPayment>      CrewPayments      { get; }
    DbSet<PayrollBatch>     PayrollBatches    { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
