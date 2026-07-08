using EventWOS.Domain.Enums;

namespace EventWOS.Domain.Interfaces;

/// <summary>Audit logging abstraction. Implementations write to DB asynchronously.</summary>
public interface IAuditLogger
{
    /// <summary>
    /// Write an audit entry. Actor defaults to ICurrentUser.UserId (the
    /// authenticated principal on the current HTTP request). Callers on
    /// the LOGIN path have no authenticated principal yet at the moment
    /// of writing — they were producing rows with a null actor which the
    /// UI rendered as "System" — so those callers must pass
    /// <paramref name="actorUserId"/> explicitly with the id of the user
    /// who just authenticated.
    /// </summary>
    Task LogAsync(
        AuditAction action,
        string entityType,
        string? entityId = null,
        object? oldValues = null,
        object? newValues = null,
        string? additionalData = null,
        Guid? actorUserId = null,
        CancellationToken cancellationToken = default);
}
