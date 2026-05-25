using EventWOS.Domain.Common;
using EventWOS.Domain.Entities;
using EventWOS.Domain.Interfaces;
using EventWOS.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Persistence;

/// <summary>
/// Main EF Core DbContext. Handles:
/// - Soft delete global query filters
/// - Automatic audit field population
/// - Domain event dispatching on SaveChanges
/// </summary>
public sealed class AppDbContext : DbContext, IAppDbContext
{
    private readonly IMediator _mediator;
    private readonly ICurrentUser _currentUser;

    public AppDbContext(
        DbContextOptions<AppDbContext> options,
        IMediator mediator,
        ICurrentUser currentUser) : base(options)
    {
        _mediator = mediator;
        _currentUser = currentUser;
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<UserRolePermission> UserRolePermissions => Set<UserRolePermission>();
    public DbSet<ManagerPermission> ManagerPermissions => Set<ManagerPermission>();
    public DbSet<OtpRequest> OtpRequests => Set<OtpRequest>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<VendorCrewMapping> VendorCrewMappings => Set<VendorCrewMapping>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    // Phase 2 — Events Module
    public DbSet<Event>            Events            => Set<Event>();
    public DbSet<EventAssignment>  EventAssignments  => Set<EventAssignment>();
    public DbSet<AttendanceRecord> AttendanceRecords => Set<AttendanceRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Apply all IEntityTypeConfiguration<T> from this assembly
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // Global soft-delete filters — automatically exclude deleted records
        modelBuilder.Entity<User>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<Role>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<Permission>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<OtpRequest>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<RefreshToken>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<UserSession>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<VendorCrewMapping>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<Event>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<EventAssignment>().HasQueryFilter(e => !e.IsDeleted);
        modelBuilder.Entity<AttendanceRecord>().HasQueryFilter(e => !e.IsDeleted);

        // Join tables reference soft-deleted principals (User, Permission).
        // Add matching filters so EF never returns orphaned rows.
        modelBuilder.Entity<ManagerPermission>().HasQueryFilter(
            e => !e.Manager.IsDeleted && !e.Permission.IsDeleted);
        modelBuilder.Entity<RolePermission>().HasQueryFilter(
            e => !e.Permission.IsDeleted);
        modelBuilder.Entity<UserRolePermission>().HasQueryFilter(
            e => !e.User.IsDeleted && !e.Permission.IsDeleted);
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        // 1. Populate audit fields
        var now = DateTime.UtcNow;
        var actorId = _currentUser.UserId;

        foreach (var entry in ChangeTracker.Entries<BaseEntity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.CreatedAt = now;
                    entry.Entity.CreatedBy = actorId;
                    break;
                case EntityState.Modified:
                    entry.Entity.UpdatedAt = now;
                    entry.Entity.UpdatedBy = actorId;
                    break;
                case EntityState.Deleted:
                    // Convert hard delete to soft delete
                    entry.State = EntityState.Modified;
                    entry.Entity.IsDeleted = true;
                    entry.Entity.DeletedAt = now;
                    entry.Entity.DeletedBy = actorId;
                    break;
            }
        }

        // 2. Collect domain events before save
        var entitiesWithEvents = ChangeTracker.Entries<BaseEntity>()
            .Where(e => e.Entity.DomainEvents.Any())
            .Select(e => e.Entity)
            .ToList();

        var result = await base.SaveChangesAsync(cancellationToken);

        // 3. Dispatch domain events after successful save
        foreach (var entity in entitiesWithEvents)
        {
            var events = entity.DomainEvents.ToList();
            entity.ClearDomainEvents();
            foreach (var domainEvent in events)
                await _mediator.Publish(domainEvent, cancellationToken);
        }

        return result;
    }
}
