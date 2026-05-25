using EventWOS.Domain.Common;

namespace EventWOS.Domain.Events;

public sealed record EventCompletedEvent(Guid EventId) : IDomainEvent;
