using MediatR;

namespace EventOpsOracle.Domain.Common;

/// <summary>Marker interface for domain events. Dispatched via MediatR.</summary>
public interface IDomainEvent : INotification
{
    DateTime OccurredAt { get; }
}
