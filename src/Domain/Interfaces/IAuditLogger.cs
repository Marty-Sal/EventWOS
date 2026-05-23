using EventWOS.Domain.Enums;

namespace EventWOS.Domain.Interfaces;

/// <summary>Audit logging abstraction. Implementations write to DB asynchronously.</summary>
public interface IAuditLogger
{
    Task LogAsync(
        AuditAction action,
        string entityType,
        string? entityId = null,
        object? oldValues = null,
        object? newValues = null,
        string? additionalData = null,
        CancellationToken cancellationToken = default);
}
