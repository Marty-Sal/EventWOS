using EventOpsOracle.Domain.Common;
using EventOpsOracle.Domain.Enums;

namespace EventOpsOracle.Domain.Events;

public sealed record UserRoleChangedEvent(Guid UserId, UserRole OldRole, UserRole NewRole) : IDomainEvent
{
    public DateTime OccurredAt { get; } = DateTime.UtcNow;
}
