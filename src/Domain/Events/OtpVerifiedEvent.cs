using EventOpsOracle.Domain.Common;

namespace EventOpsOracle.Domain.Events;

public sealed record OtpVerifiedEvent(Guid OtpRequestId, string Mobile, Guid UserId) : IDomainEvent
{
    public DateTime OccurredAt { get; } = DateTime.UtcNow;
}
