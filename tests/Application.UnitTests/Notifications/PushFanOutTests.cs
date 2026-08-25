using EventOpsOracle.Application.Notifications.Abstractions;
using EventOpsOracle.Application.Notifications.Channels;
using EventOpsOracle.Application.Notifications.Contracts;
using EventOpsOracle.Application.Notifications.Rendering;
using EventOpsOracle.Domain.Entities;
using EventOpsOracle.Domain.Enums;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EventOpsOracle.Application.UnitTests.Notifications;

/// <summary>
/// The fan-out rules. Push is the only channel where one delivery row means
/// several external calls, so "what does the delivery record when the phone
/// worked and the laptop is dead" is the question that actually decides whether
/// people get told things.
/// </summary>
public class PushFanOutTests
{
    private static readonly Guid Recipient = Guid.NewGuid();

    // ---- doubles ----------------------------------------------------------

    private sealed class FakeProvider : IPushNotificationProvider
    {
        private readonly Queue<PushSendResult> _results;
        public FakeProvider(params PushSendResult[] results) => _results = new Queue<PushSendResult>(results);

        public List<PushMessage> Sent { get; } = new();
        public PushProvider Provider    => PushProvider.WebPush;
        public string ProviderName      => "WebPush";
        public bool IsConfigured        { get; set; } = true;

        public Task<PushSendResult> SendAsync(PushMessage message, PushEndpoint endpoint, CancellationToken ct = default)
        {
            Sent.Add(message);
            return Task.FromResult(_results.Count > 0 ? _results.Dequeue() : PushSendResult.Accepted());
        }
    }

    private sealed class ThrowingProvider : IPushNotificationProvider
    {
        public PushProvider Provider => PushProvider.WebPush;
        public string ProviderName   => "WebPush";
        public bool IsConfigured     => true;
        public Task<PushSendResult> SendAsync(PushMessage message, PushEndpoint endpoint, CancellationToken ct = default)
            => throw new InvalidOperationException("provider bug");
    }

    private sealed class FakeStore : IPushRegistrationStore
    {
        public List<PushEndpoint> Endpoints { get; set; } = new();
        public int UnreadCount { get; set; }
        public List<PushEndpointOutcome> Recorded { get; } = new();

        public Task<IReadOnlyList<PushEndpoint>> GetActiveEndpointsAsync(Guid userId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PushEndpoint>>(Endpoints);

        public Task<int> GetUnreadCountAsync(Guid userId, CancellationToken ct = default)
            => Task.FromResult(UnreadCount);

        public Task ApplyOutcomesAsync(IReadOnlyCollection<PushEndpointOutcome> outcomes, CancellationToken ct = default)
        {
            Recorded.AddRange(outcomes);
            return Task.CompletedTask;
        }
    }

    private static PushEndpoint Endpoint(string suffix = "a") =>
        new(Guid.NewGuid(), PushProvider.WebPush, $"https://push.example/{suffix}", "p256dh", "auth", null);

    private static NotificationSendContext Context()
    {
        var notification = new Notification(
            Recipient, NotificationTemplateCodes.CrewAssignment, NotificationPriority.Normal,
            "{}", "key-1");
        // Destination is null on purpose: a Push delivery addresses the user, and
        // the sender resolves that user's devices at send time.
        var delivery = notification.AddDelivery(NotificationChannel.Push, null, "WebPush", 1);
        var template = new NotificationTemplate(
            NotificationTemplateCodes.CrewAssignment, NotificationChannel.Push,
            "Mumbai Event - today at 6:00 PM", subject: "New Shift Assigned");
        var rendered = new RenderedNotification(
            "New Shift Assigned", "Mumbai Event - today at 6:00 PM",
            Array.Empty<string>(), Array.Empty<string>());

        return new NotificationSendContext(
            notification, delivery, template, rendered,
            new Dictionary<string, string?> { ["EventName"] = "Mumbai Event" });
    }

    private static PushNotificationSender Sender(IPushNotificationProvider provider, IPushRegistrationStore store)
        => new(new[] { provider }, store, NullLogger<PushNotificationSender>.Instance);

    // ---- the rules --------------------------------------------------------

    [Fact]
    public async Task No_registrations_is_skipped_not_failed()
    {
        // Most users never enable push. Recording that as a failure would bury
        // the real failures in noise.
        var store  = new FakeStore();
        var result = await Sender(new FakeProvider(), store).SendAsync(Context());

        result.Outcome.Should().Be(ChannelSendOutcome.Skipped);
        result.Detail.Should().Contain("no active push registrations");
    }

    [Fact]
    public async Task One_live_device_is_enough_to_call_it_accepted()
    {
        var store = new FakeStore { Endpoints = { Endpoint("phone"), Endpoint("laptop") } };
        var provider = new FakeProvider(PushSendResult.Accepted("msg-1"), PushSendResult.Gone("410 Gone"));

        var result = await Sender(provider, store).SendAsync(Context());

        result.Outcome.Should().Be(ChannelSendOutcome.Accepted);
        result.ProviderMessageId.Should().Be("msg-1");
        result.Detail.Should().Contain("1 of 2");
    }

    [Fact]
    public async Task A_dead_endpoint_is_retired_even_though_the_delivery_succeeded()
    {
        var phone  = Endpoint("phone");
        var laptop = Endpoint("laptop");
        var store  = new FakeStore { Endpoints = { phone, laptop } };
        var provider = new FakeProvider(PushSendResult.Accepted(), PushSendResult.Gone("410 Gone"));

        await Sender(provider, store).SendAsync(Context());

        store.Recorded.Should().ContainSingle(o =>
            o.RegistrationId == laptop.RegistrationId && o.Outcome == PushSendOutcome.EndpointGone);
        store.Recorded.Should().ContainSingle(o =>
            o.RegistrationId == phone.RegistrationId && o.Outcome == PushSendOutcome.Accepted);
    }

    [Fact]
    public async Task Every_endpoint_gone_is_permanent()
    {
        var store = new FakeStore { Endpoints = { Endpoint("a"), Endpoint("b") } };
        var provider = new FakeProvider(PushSendResult.Gone("410"), PushSendResult.Gone("404"));

        var result = await Sender(provider, store).SendAsync(Context());

        result.Outcome.Should().Be(ChannelSendOutcome.PermanentFailure);
        result.Detail.Should().Contain("no longer valid");
    }

    [Fact]
    public async Task An_outstanding_transient_failure_retries_the_whole_delivery()
    {
        // Nobody was reached and one endpoint might work later, so the delivery
        // goes back on the backoff rather than being written off.
        var store = new FakeStore { Endpoints = { Endpoint("a"), Endpoint("b") } };
        var provider = new FakeProvider(PushSendResult.Gone("410"), PushSendResult.Transient("503"));

        var result = await Sender(provider, store).SendAsync(Context());

        result.Outcome.Should().Be(ChannelSendOutcome.TransientFailure);
    }

    [Fact]
    public async Task A_provider_that_throws_does_not_stop_the_other_devices()
    {
        var store = new FakeStore { Endpoints = { Endpoint("a"), Endpoint("b") } };

        var result = await Sender(new ThrowingProvider(), store).SendAsync(Context());

        result.Outcome.Should().Be(ChannelSendOutcome.TransientFailure, "a provider bug is not a verdict on the subscription");
        store.Recorded.Should().HaveCount(2);
        store.Recorded.Should().OnlyContain(o => o.Outcome == PushSendOutcome.TransientFailure);
    }

    [Fact]
    public async Task Cancellation_during_shutdown_is_not_recorded_as_a_failure()
    {
        var store = new FakeStore { Endpoints = { Endpoint("a") } };
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await Sender(new CancellingProvider(), store).SendAsync(Context(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        store.Recorded.Should().BeEmpty("the attempt never reached a verdict");
    }

    private sealed class CancellingProvider : IPushNotificationProvider
    {
        public PushProvider Provider => PushProvider.WebPush;
        public string ProviderName   => "WebPush";
        public bool IsConfigured     => true;
        public Task<PushSendResult> SendAsync(PushMessage message, PushEndpoint endpoint, CancellationToken ct = default)
            => throw new OperationCanceledException(ct);
    }

    [Fact]
    public async Task A_registration_for_an_unconfigured_transport_is_transient()
    {
        // An FCM row while only VAPID is switched on. The subscription is fine;
        // the configuration is not, and that is fixable.
        var store = new FakeStore
        {
            Endpoints = { new PushEndpoint(Guid.NewGuid(), PushProvider.Fcm, null, null, null, "fcm-token") }
        };

        var result = await Sender(new FakeProvider(), store).SendAsync(Context());

        result.Outcome.Should().Be(ChannelSendOutcome.TransientFailure);
        result.Detail.Should().Contain("Fcm");
    }

    // ---- payload ----------------------------------------------------------

    [Fact]
    public async Task The_badge_is_the_servers_unread_count_not_the_number_of_pushes()
    {
        var store = new FakeStore { Endpoints = { Endpoint("a"), Endpoint("b"), Endpoint("c") }, UnreadCount = 5 };
        var provider = new FakeProvider();

        await Sender(provider, store).SendAsync(Context());

        provider.Sent.Should().HaveCount(3);
        provider.Sent.Should().OnlyContain(m => m.BadgeCount == 5,
            "three devices were pushed, but the user has five unread notifications");
    }

    [Fact]
    public async Task The_payload_carries_title_body_type_and_a_deep_link()
    {
        var store = new FakeStore { Endpoints = { Endpoint() }, UnreadCount = 1 };
        var provider = new FakeProvider();

        await Sender(provider, store).SendAsync(Context());

        var sent = provider.Sent.Single();
        sent.Title.Should().Be("New Shift Assigned");
        sent.Body.Should().Be("Mumbai Event - today at 6:00 PM");
        sent.NotificationType.Should().Be(NotificationTemplateCodes.CrewAssignment);
        sent.DeepLink.Should().Be("/my-assignments");
        sent.Priority.Should().Be(NotificationPriority.Normal);
    }

    [Fact]
    public async Task A_template_without_a_subject_still_gets_a_title()
    {
        var store    = new FakeStore { Endpoints = { Endpoint() } };
        var provider = new FakeProvider();
        var context  = Context();
        var untitled = context with
        {
            Message = new RenderedNotification(null, "Your shift starts in an hour", Array.Empty<string>(), Array.Empty<string>())
        };

        await Sender(provider, store).SendAsync(untitled);

        provider.Sent.Single().Title.Should().Be("OpsOracle", "a titleless notification reads as broken on every platform");
    }

    [Fact]
    public void The_sender_is_unconfigured_when_no_provider_is()
    {
        var store    = new FakeStore();
        var provider = new FakeProvider { IsConfigured = false };

        Sender(provider, store).IsConfigured.Should().BeFalse("push must not be queued when it can only fail");
    }
}
