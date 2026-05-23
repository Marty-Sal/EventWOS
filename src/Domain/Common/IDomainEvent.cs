using MediatR;

namespace EventWOS.Domain.Common;

/// <summary>Marker interface for domain events. Dispatched via MediatR.</summary>
public interface IDomainEvent : INotification
{
    DateTime OccurredAt { get; }
}
