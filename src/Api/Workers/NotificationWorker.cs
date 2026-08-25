using EventOpsOracle.Application.Notifications.Abstractions;
using EventOpsOracle.Application.Notifications.Services;
using EventOpsOracle.Infrastructure.Notifications;
using Microsoft.Extensions.Options;

namespace EventOpsOracle.Api.Workers;

/// <summary>
/// The loop that makes notifications actually happen: claim due outbox rows,
/// expand them, claim due deliveries, send them, and periodically recover work
/// stranded by a worker that died.
///
/// Polling rather than a broker. At OpsOracle's volume the queue depth is small
/// and the tables are indexed for exactly this query, so a five-second poll on
/// Postgres is cheaper to run, deploy and reason about than adding a message
/// broker to the estate -- and it inherits Postgres' durability for free.
///
/// Each pass runs in its own DI scope: this is a singleton hosted service and
/// the DbContext is scoped, so sharing one across passes would leak a growing
/// change tracker and eventually turn a stale entity into a wrong notification.
/// </summary>
public sealed class NotificationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly NotificationWorkerOptions _options;
    private readonly ILogger<NotificationWorker> _logger;
    private readonly string _workerId;

    public NotificationWorker(
        IServiceScopeFactory scopes,
        IOptions<NotificationWorkerOptions> options,
        ILogger<NotificationWorker> logger)
    {
        _scopes  = scopes;
        _options = options.Value;
        _logger  = logger;

        // Identifies which instance holds a row -- the first thing you want when
        // a delivery is stuck and three replicas are running.
        _workerId = $"{Environment.MachineName}:{Guid.NewGuid():N}"[..40];
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogWarning(
                "Notification worker is DISABLED (Notifications:Enabled=false). Notifications will queue in Postgres and not be sent.");
            return;
        }

        _logger.LogInformation(
            "Notification worker {WorkerId} started (outbox batch {OutboxBatch}, delivery batch {DeliveryBatch}, idle poll {IdleSeconds}s)",
            _workerId, _options.OutboxBatchSize, _options.DeliveryBatchSize, _options.IdlePollSeconds);

        // Sweep on startup: if this process is replacing one that was killed
        // mid-send, its rows are stranded in Processing right now.
        var nextSweep = DateTime.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            var handled = 0;

            try
            {
                if (DateTime.UtcNow >= nextSweep)
                {
                    await SweepStaleLocksAsync(stoppingToken);
                    nextSweep = DateTime.UtcNow.AddMinutes(_options.StaleSweepMinutes);
                }

                handled = await RunPassAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never let one bad pass kill the loop: a dead worker means every
                // notification silently stops, which is the failure mode this
                // whole subsystem exists to prevent. Back off and continue.
                _logger.LogError(ex, "Notification worker pass failed; continuing after backoff");
                await SafeDelayAsync(TimeSpan.FromSeconds(_options.IdlePollSeconds * 2), stoppingToken);
                continue;
            }

            var delay = handled > 0
                ? TimeSpan.FromMilliseconds(_options.BusyPollMilliseconds)
                : TimeSpan.FromSeconds(_options.IdlePollSeconds);

            await SafeDelayAsync(delay, stoppingToken);
        }

        _logger.LogInformation("Notification worker {WorkerId} stopping", _workerId);
    }

    private async Task<int> RunPassAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();

        var outbox     = scope.ServiceProvider.GetRequiredService<OutboxProcessor>();
        var deliveries = scope.ServiceProvider.GetRequiredService<DeliveryProcessor>();

        // Outbox first: it creates the delivery rows the second stage consumes,
        // so a fresh notification can be expanded and sent in the same pass.
        var expanded = await outbox.ProcessBatchAsync(_workerId, _options.OutboxBatchSize, ct);
        var sent     = await deliveries.ProcessBatchAsync(_workerId, _options.DeliveryBatchSize, ct);

        return expanded + sent;
    }

    private async Task SweepStaleLocksAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<INotificationWorkQueue>();

        await queue.ReleaseStaleLocksAsync(TimeSpan.FromMinutes(_options.LockTimeoutMinutes), ct);
    }

    private static async Task SafeDelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); }
        catch (OperationCanceledException) { /* shutting down */ }
    }
}
