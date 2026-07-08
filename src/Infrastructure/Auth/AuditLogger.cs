using EventWOS.Domain.Entities;
using EventWOS.Domain.Enums;
using EventWOS.Domain.Interfaces;
using EventWOS.Persistence;
using System.Text.Json;

namespace EventWOS.Infrastructure.Auth;

/// <summary>
/// Writes audit entries directly to DB using a scoped DbContext.
/// Uses System.Text.Json for serialising value snapshots.
/// </summary>
public sealed class AuditLogger : IAuditLogger
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _currentUser;

    public AuditLogger(AppDbContext db, ICurrentUser currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    public async Task LogAsync(
        AuditAction action,
        string entityType,
        string? entityId = null,
        object? oldValues = null,
        object? newValues = null,
        string? additionalData = null,
        Guid? actorUserId = null,
        CancellationToken cancellationToken = default)
    {
        // Actor: explicit override wins (used by the LOGIN path, where
        // the request has no bearer token yet so ICurrentUser.UserId is
        // null even though we know exactly which user is being logged
        // in). Falls back to the ambient current user for every other
        // caller so nothing else in the codebase needs to change.
        var entry = new AuditLog(
            action,
            actorUserId ?? _currentUser.UserId,
            _currentUser.IpAddress,
            entityType,
            entityId,
            oldValues is null ? null : JsonSerializer.Serialize(oldValues),
            newValues is null ? null : JsonSerializer.Serialize(newValues),
            additionalData);

        _db.AuditLogs.Add(entry);
        await _db.SaveChangesAsync(cancellationToken);
    }
}
