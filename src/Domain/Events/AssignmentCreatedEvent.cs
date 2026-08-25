using EventOpsOracle.Domain.Common;

namespace EventOpsOracle.Domain.Events;

public sealed record AssignmentCreatedEvent(Guid AssignmentId, Guid EventId, Guid CrewId) : IDomainEvent
{
    public DateTime OccurredAt { get; } = DateTime.UtcNow;
}
