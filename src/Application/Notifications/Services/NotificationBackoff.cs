namespace EventWOS.Application.Notifications.Services;

/// <summary>
/// Retry schedule for failed sends: exponential backoff with jitter, and a hard
/// attempt ceiling.
///
/// The jitter matters more than it looks. Fan-out means hundreds of deliveries
/// fail at the same instant when a provider goes down, and a pure exponential
/// schedule would send them all back at the provider together, repeatedly --
/// a self-inflicted thundering herd against a service already in trouble.
/// Spreading each retry across a window breaks that up.
/// </summary>
public static class NotificationBackoff
{
    /// <summary>
    /// After this many attempts a delivery is failed for good. Five attempts
    /// across roughly 15 minutes covers every realistic transient outage; past
    /// that the message is usually stale anyway -- a shift reminder delivered
    /// hours late is worse than an honest failure an operator can see.
    /// </summary>
    public const int MaxAttempts = 5;

    private static readonly TimeSpan[] BaseDelays =
    {
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
    };

    /// <summary>Jitter as a fraction of the base delay (+/- 20%).</summary>
    private const double JitterFraction = 0.2;

    public static bool ShouldRetry(int attemptCount) => attemptCount < MaxAttempts;

    /// <summary>
    /// When the next attempt is due. <paramref name="attemptCount"/> is the
    /// number of attempts already made, and <paramref name="random"/> is
    /// injectable so tests are deterministic.
    /// </summary>
    public static DateTime NextAttemptAt(int attemptCount, DateTime nowUtc, Random? random = null)
    {
        var index = Math.Clamp(attemptCount - 1, 0, BaseDelays.Length - 1);
        var baseDelay = BaseDelays[index];

        var rng    = random ?? Random.Shared;
        var factor = 1.0 + ((rng.NextDouble() * 2.0 - 1.0) * JitterFraction);

        return nowUtc.AddSeconds(baseDelay.TotalSeconds * factor);
    }
}
