using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using System.Security.Claims;

namespace EventWOS.Api.Hubs;

/// <summary>
/// Real-time notification hub. Authenticated users join their own user group
/// and their role group. Admins can broadcast to all.
/// </summary>
[Authorize]
public sealed class NotificationHub : Hub
{
    private readonly ILogger<NotificationHub> _logger;

    public NotificationHub(ILogger<NotificationHub> logger) => _logger = logger;

    public override async Task OnConnectedAsync()
    {
        var userId = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? Context.User?.FindFirstValue("sub");
        var role   = Context.User?.FindFirstValue("role") ?? "Unknown";

        if (userId is not null)
        {
            // Each user has a private group — allows targeted pushes
            await Groups.AddToGroupAsync(Context.ConnectionId, $"user:{userId}");
            // Role-based broadcast group
            await Groups.AddToGroupAsync(Context.ConnectionId, $"role:{role.ToLower()}");
        }

        _logger.LogInformation("Hub connected: {ConnectionId} | User: {UserId} | Role: {Role}",
            Context.ConnectionId, userId, role);

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("Hub disconnected: {ConnectionId}", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    // ─── Client-callable methods ──────────────────────────────────────────

    /// <summary>Ping/pong for connection health check.</summary>
    public async Task Ping() =>
        await Clients.Caller.SendAsync("Pong", DateTime.UtcNow);

    /// <summary>Admin-only: broadcast a system notification to all connected users.</summary>
    public async Task BroadcastSystem(string message)
    {
        var role = Context.User?.FindFirstValue("role");
        if (role != "Admin") throw new HubException("Unauthorized.");
        await Clients.All.SendAsync("SystemNotification", new { message, timestamp = DateTime.UtcNow });
    }
}

/// <summary>Service for pushing notifications from backend handlers.</summary>
public interface INotificationPusher
{
    Task PushToUserAsync(Guid userId, string eventName, object payload, CancellationToken ct = default);
    Task PushToRoleAsync(string role, string eventName, object payload, CancellationToken ct = default);
    Task PushToAllAsync(string eventName, object payload, CancellationToken ct = default);
}

public sealed class SignalRNotificationPusher : INotificationPusher
{
    private readonly IHubContext<NotificationHub> _hub;

    public SignalRNotificationPusher(IHubContext<NotificationHub> hub) => _hub = hub;

    public Task PushToUserAsync(Guid userId, string eventName, object payload, CancellationToken ct = default) =>
        _hub.Clients.Group($"user:{userId}").SendAsync(eventName, payload, ct);

    public Task PushToRoleAsync(string role, string eventName, object payload, CancellationToken ct = default) =>
        _hub.Clients.Group($"role:{role.ToLower()}").SendAsync(eventName, payload, ct);

    public Task PushToAllAsync(string eventName, object payload, CancellationToken ct = default) =>
        _hub.Clients.All.SendAsync(eventName, payload, ct);
}
