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
    DbSet<CrewGroup>        CrewGroups        { get; }
    DbSet<CrewGroupMember>  CrewGroupMembers  { get; }
    DbSet<EventWOS.Domain.Entities.ScopeOfWork>      ScopesOfWork      { get; }
    DbSet<AuditLog>         AuditLogs         { get; }

    // Phase 2 — Events Module
    DbSet<Event>            Events            { get; }
    DbSet<EventShift>       EventShifts       { get; }
    DbSet<VendorShiftAllocation> VendorShiftAllocations { get; }
    DbSet<EventAssignment>  EventAssignments  { get; }
    DbSet<AttendanceRecord> AttendanceRecords { get; }
    DbSet<CrewPayment>      CrewPayments      { get; }
    DbSet<PayrollBatch>     PayrollBatches    { get; }
    // QR-verified check-in handshake table.
    DbSet<PendingCheckIn>   PendingCheckIns   { get; }

    // File & Image Storage module
    DbSet<FileDocument>     FileDocuments     { get; }

    // Settings module — Venue catalog.
    DbSet<Venue>             Venues            { get; }
    DbSet<TermsAndConditions> TermsAndConditions { get; }
    DbSet<TermsAcceptance>    TermsAcceptances    { get; }

    // Reference data — canonical India states + union territories list.
    DbSet<IndianState> IndianStates { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
