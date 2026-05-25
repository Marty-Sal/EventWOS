namespace EventWOS.Application.Interfaces;

/// <summary>
/// Abstraction for pushing real-time notifications to connected clients.
/// Implemented by SignalRNotificationPusher in the Api layer.
/// </summary>
public interface INotificationPusher
{
    Task PushToUserAsync(Guid userId, string eventName, object payload, CancellationToken ct = default);
    Task PushToRoleAsync(string role, string eventName, object payload, CancellationToken ct = default);
    Task PushToAllAsync(string eventName, object payload, CancellationToken ct = default);
}
