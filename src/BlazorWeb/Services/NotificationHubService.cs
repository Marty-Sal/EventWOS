using EventWOS.BlazorWeb.Auth;
using Microsoft.AspNetCore.SignalR.Client;

namespace EventWOS.BlazorWeb.Services;

/// <summary>
/// Manages the SignalR connection lifecycle. Auto-reconnects on drop.
/// Exposes strongly-typed events for all backend push notifications.
/// </summary>
public sealed class NotificationHubService : IAsyncDisposable
{
    private HubConnection? _connection;
    private readonly AppAuthStateProvider _auth;
    private readonly IConfiguration       _config;

    // ─── Events ────────────────────────────────────────────────────────────
    public event Action<string>?               SystemNotificationReceived;
    public event Action<NotificationPayload>?  AssignmentInviteReceived;      // → crew
    public event Action<NotificationPayload>?  CrewAcceptedReceived;          // → vendor
    public event Action<NotificationPayload>?  CrewDeclinedReceived;          // → vendor
    public event Action<NotificationPayload>?  VendorApprovedReceived;        // → crew
    public event Action<NotificationPayload>?  VendorRejectedReceived;        // → crew
    public event Action<NotificationPayload>?  PendingManagerApprovalReceived;// → managers
    public event Action<NotificationPayload>?  ManagerApprovedReceived;       // → crew
    public event Action<NotificationPayload>?  ManagerRejectedReceived;       // → crew
    public event Action?                       ConnectionStateChanged;

    public HubConnectionState State      => _connection?.State ?? HubConnectionState.Disconnected;
    public bool               IsConnected => State == HubConnectionState.Connected;

    public NotificationHubService(AppAuthStateProvider auth, IConfiguration config)
    {
        _auth   = auth;
        _config = config;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_connection is { State: HubConnectionState.Connected }) return;

        var token  = await _auth.GetAccessTokenAsync();
        var hubUrl = $"{_config["ApiBaseUrl"]}/hubs/notifications?access_token={token}";

        _connection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect([
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(3),
                TimeSpan.FromSeconds(10)
            ])
            .Build();

        // System broadcast
        _connection.On<object>("SystemNotification",
            msg => SystemNotificationReceived?.Invoke(msg?.ToString() ?? ""));

        // Crew invitation (→ crew member)
        _connection.On<NotificationPayload>("AssignmentInvite",
            payload => AssignmentInviteReceived?.Invoke(payload));

        // Crew response (→ vendor)
        _connection.On<NotificationPayload>("CrewAccepted",
            payload => CrewAcceptedReceived?.Invoke(payload));
        _connection.On<NotificationPayload>("CrewDeclined",
            payload => CrewDeclinedReceived?.Invoke(payload));

        // Vendor review (→ crew)
        _connection.On<NotificationPayload>("VendorApprovedYou",
            payload => VendorApprovedReceived?.Invoke(payload));
        _connection.On<NotificationPayload>("VendorRejectedYou",
            payload => VendorRejectedReceived?.Invoke(payload));

        // Manager approval queue (→ managers/admins)
        _connection.On<NotificationPayload>("PendingManagerApproval",
            payload => PendingManagerApprovalReceived?.Invoke(payload));

        // Manager final decision (→ crew)
        _connection.On<NotificationPayload>("ManagerApprovedYou",
            payload => ManagerApprovedReceived?.Invoke(payload));
        _connection.On<NotificationPayload>("ManagerRejectedYou",
            payload => ManagerRejectedReceived?.Invoke(payload));

        _connection.Reconnected += _ => { ConnectionStateChanged?.Invoke(); return Task.CompletedTask; };
        _connection.Closed      += _ => { ConnectionStateChanged?.Invoke(); return Task.CompletedTask; };

        await _connection.StartAsync(ct);
        ConnectionStateChanged?.Invoke();
    }

    public async Task StopAsync() =>
        await (_connection?.StopAsync() ?? Task.CompletedTask);

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
            await _connection.DisposeAsync();
    }
}

/// <summary>Generic payload for assignment-related push notifications.</summary>
public sealed record NotificationPayload(
    Guid?   AssignmentId  = null,
    string? EventTitle    = null,
    string? VendorName    = null,
    string? CrewName      = null,
    string? Reason        = null,
    DateTime? EventStart  = null
);
