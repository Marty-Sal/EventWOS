using EventOpsOracle.Domain.Common;

namespace EventOpsOracle.Domain.Events;

public sealed record EventCompletedEvent(Guid EventId) : IDomainEvent
{
    public DateTime OccurredAt { get; } = DateTime.UtcNow;
}
