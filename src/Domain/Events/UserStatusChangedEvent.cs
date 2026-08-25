using EventOpsOracle.Domain.Common;
using EventOpsOracle.Domain.Enums;

namespace EventOpsOracle.Domain.Events;

public sealed record UserStatusChangedEvent(Guid UserId, Guid ChangedByAdminId, UserStatus NewStatus) : IDomainEvent
{
    public DateTime OccurredAt { get; } = DateTime.UtcNow;
}
