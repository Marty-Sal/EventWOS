using EventWOS.BlazorWeb.Auth;
using Microsoft.AspNetCore.SignalR.Client;

namespace EventWOS.BlazorWeb.Services;

/// <summary>
/// Manages the SignalR connection lifecycle. Auto-reconnects on drop.
/// Components subscribe to events via the Received* callbacks.
/// </summary>
public sealed class NotificationHubService : IAsyncDisposable
{
    private HubConnection? _connection;
    private readonly AppAuthStateProvider _auth;
    private readonly IConfiguration _config;

    public event Action<string>? SystemNotificationReceived;
    public event Action? ConnectionStateChanged;

    public HubConnectionState State => _connection?.State ?? HubConnectionState.Disconnected;
    public bool IsConnected => State == HubConnectionState.Connected;

    public NotificationHubService(AppAuthStateProvider auth, IConfiguration config)
    {
        _auth = auth;
        _config = config;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_connection is { State: HubConnectionState.Connected }) return;

        var token = await _auth.GetAccessTokenAsync();
        var hubUrl = $"{_config["ApiBaseUrl"]}/hubs/notifications?access_token={token}";

        _connection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10) })
            .Build();

        _connection.On<string>("SystemNotification", msg =>
        {
            SystemNotificationReceived?.Invoke(msg);
        });

        _connection.Reconnected += _ => { ConnectionStateChanged?.Invoke(); return Task.CompletedTask; };
        _connection.Closed += _ => { ConnectionStateChanged?.Invoke(); return Task.CompletedTask; };

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
