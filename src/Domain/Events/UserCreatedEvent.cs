using EventWOS.Domain.Common;
using EventWOS.Domain.Enums;

namespace EventWOS.Domain.Events;

public sealed record UserCreatedEvent(Guid UserId, string Mobile, UserRole Role) : IDomainEvent
{
    public DateTime OccurredAt { get; } = DateTime.UtcNow;
}
