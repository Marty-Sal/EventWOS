namespace EventWOS.Infrastructure.Notifications;

/// <summary>
/// Worker tuning, bound from the "Notifications" configuration section (so
/// Railway can set Notifications__Enabled=false, Notifications__PollSeconds=10,
/// and so on without a code change).
///
/// The worker runs in-process inside the API today. Everything it needs is
/// behind <see cref="Enabled"/>, so moving it to its own service later means
/// running the same image with the flag off on the API and on in the worker --
/// no rewrite, and no risk of two deployments both polling by accident.
/// </summary>
public sealed class NotificationWorkerOptions
{
    public const string SectionName = "Notifications";

    /// <summary>Master switch for the background loop. The dispatcher keeps queueing either way.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Outbox rows claimed per pass.</summary>
    public int OutboxBatchSize { get; set; } = 20;

    /// <summary>
    /// Delivery rows claimed per pass. Modest on purpose: each row can mean an
    /// HTTP call to a provider, and a large batch would hold its locks for the
    /// length of the slowest call in the batch.
    /// </summary>
    public int DeliveryBatchSize { get; set; } = 25;

    /// <summary>Wait between passes when there was nothing to do.</summary>
    public int IdlePollSeconds { get; set; } = 5;

    /// <summary>
    /// Wait between passes when the last pass was full -- there is probably more
    /// waiting, so drain quickly instead of sleeping the full idle interval.
    /// </summary>
    public int BusyPollMilliseconds { get; set; } = 250;

    /// <summary>
    /// How long a row may sit in Processing before it is assumed abandoned.
    /// Must comfortably exceed the slowest provider call, or the sweep would
    /// re-queue work that is still in flight and send it twice.
    /// </summary>
    public int LockTimeoutMinutes { get; set; } = 5;

    /// <summary>How often the stale-lock sweep runs.</summary>
    public int StaleSweepMinutes { get; set; } = 2;
}
