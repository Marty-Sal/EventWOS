using EventWOS.Domain.Common;

namespace EventWOS.Domain.Events;

public sealed record AssignmentCreatedEvent(Guid AssignmentId, Guid EventId, Guid CrewId) : IDomainEvent
{
    public DateTime OccurredAt { get; } = DateTime.UtcNow;
}
