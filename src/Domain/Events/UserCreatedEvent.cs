using EventOpsOracle.Domain.Common;
using EventOpsOracle.Domain.Enums;

namespace EventOpsOracle.Domain.Events;

public sealed record UserCreatedEvent(Guid UserId, string Mobile, UserRole Role) : IDomainEvent
{
    public DateTime OccurredAt { get; } = DateTime.UtcNow;
}
