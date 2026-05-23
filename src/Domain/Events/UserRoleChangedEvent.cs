using EventWOS.Domain.Common;
using EventWOS.Domain.Enums;

namespace EventWOS.Domain.Events;

public sealed record UserRoleChangedEvent(Guid UserId, UserRole OldRole, UserRole NewRole) : IDomainEvent
{
    public DateTime OccurredAt { get; } = DateTime.UtcNow;
}
