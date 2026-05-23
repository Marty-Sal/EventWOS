using EventWOS.Domain.Common;
using EventWOS.Domain.Enums;

namespace EventWOS.Domain.Entities;

/// <summary>
/// Immutable audit trail record. Never updated or deleted.
/// Captures every sensitive action in the system.
/// </summary>
public sealed class AuditLog : BaseEntity
{
    private AuditLog() { }

    public AuditLog(
        AuditAction action,
        Guid? performedByUserId,
        string? performedByIp,
        string entityType,
        string? entityId,
        string? oldValues,
        string? newValues,
        string? additionalData = null)
    {
        Action = action;
        PerformedByUserId = performedByUserId;
        PerformedByIp = performedByIp;
        EntityType = entityType;
        EntityId = entityId;
        OldValues = oldValues;
        NewValues = newValues;
        AdditionalData = additionalData;
        OccurredAt = DateTime.UtcNow;
    }

    public AuditAction Action { get; private set; }
    public Guid? PerformedByUserId { get; private set; }
    public string? PerformedByIp { get; private set; }
    public string EntityType { get; private set; } = default!;
    public string? EntityId { get; private set; }
    public string? OldValues { get; private set; }   // JSON snapshot
    public string? NewValues { get; private set; }   // JSON snapshot
    public string? AdditionalData { get; private set; }
    public DateTime OccurredAt { get; private set; }
}
