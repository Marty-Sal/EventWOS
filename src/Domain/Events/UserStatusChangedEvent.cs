using EventWOS.Domain.Common;
using EventWOS.Domain.Enums;

namespace EventWOS.Domain.Events;

public sealed record UserStatusChangedEvent(Guid UserId, Guid ChangedByAdminId, UserStatus NewStatus) : IDomainEvent
{
    public DateTime OccurredAt { get; } = DateTime.UtcNow;
}
