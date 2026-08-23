using EventWOS.Application.Notifications.Services;
using FluentAssertions;
using Xunit;

namespace EventWOS.Application.UnitTests.Notifications;

/// <summary>
/// Covers the retry schedule. The jitter test is the important one: without it,
/// a provider outage during a fan-out sends every failed delivery back at the
/// provider in the same instant, repeatedly.
/// </summary>
public class NotificationBackoffTests
{
    private static readonly DateTime Now = new(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(4, true)]
    [InlineData(5, false)]
    [InlineData(9, false)]
    public void Stops_retrying_at_the_attempt_ceiling(int attempts, bool expected)
        => NotificationBackoff.ShouldRetry(attempts).Should().Be(expected);

    [Fact]
    public void Delay_grows_with_each_attempt()
    {
        // Fixed seed: assert the shape of the schedule, not the jitter.
        var first  = NotificationBackoff.NextAttemptAt(1, Now, new Random(1));
        var second = NotificationBackoff.NextAttemptAt(2, Now, new Random(1));
        var third  = NotificationBackoff.NextAttemptAt(3, Now, new Random(1));
        var fourth = NotificationBackoff.NextAttemptAt(4, Now, new Random(1));

        first.Should().BeAfter(Now);
        second.Should().BeAfter(first);
        third.Should().BeAfter(second);
        fourth.Should().BeAfter(third);
    }

    [Fact]
    public void Jitter_spreads_simultaneous_failures_instead_of_bunching_them()
    {
        // 200 deliveries failing at the same moment must not all come back at
        // the same moment.
        var times = Enumerable.Range(0, 200)
            .Select(_ => NotificationBackoff.NextAttemptAt(1, Now))
            .ToList();

        times.Distinct().Should().HaveCountGreaterThan(150);

        var spread = times.Max() - times.Min();
        spread.Should().BeGreaterThan(TimeSpan.FromSeconds(5), "a herd of retries should be smeared across a window");
    }

    [Fact]
    public void Jitter_stays_within_twenty_percent_of_the_base_delay()
    {
        // Bounded both ways: never retries almost immediately, never drifts so
        // far that a time-sensitive shift alert misses its window.
        for (var i = 0; i < 500; i++)
        {
            var at = NotificationBackoff.NextAttemptAt(1, Now);
            (at - Now).Should().BeGreaterThan(TimeSpan.FromSeconds(23))
                               .And.BeLessThan(TimeSpan.FromSeconds(37));
        }
    }

    [Fact]
    public void Attempt_counts_beyond_the_schedule_reuse_the_longest_delay()
    {
        // Guards the array indexing: an unexpected attempt count must not throw
        // inside the worker loop.
        var act = () => NotificationBackoff.NextAttemptAt(99, Now, new Random(7));
        act.Should().NotThrow();

        NotificationBackoff.NextAttemptAt(99, Now, new Random(7))
            .Should().BeCloseTo(NotificationBackoff.NextAttemptAt(4, Now, new Random(7)), TimeSpan.FromSeconds(1));
    }
}
