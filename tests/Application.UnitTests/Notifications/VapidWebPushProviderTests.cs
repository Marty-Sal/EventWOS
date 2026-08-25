using System.Net;
using System.Text.Json;
using EventOpsOracle.Application.Notifications.Abstractions;
using EventOpsOracle.Application.Notifications.Contracts;
using EventOpsOracle.Domain.Enums;
using EventOpsOracle.Infrastructure.Notifications.Channels;
using FluentAssertions;
using Lib.Net.Http.WebPush;
using AppPushMessage = EventOpsOracle.Application.Notifications.Contracts.PushMessage;
using Xunit;

namespace EventOpsOracle.Application.UnitTests.Notifications;

/// <summary>
/// How a push service's answer is read, and what actually goes on the wire.
/// The classification is the whole retry policy for this channel: get 410 wrong
/// and we hammer dead endpoints forever; get 503 wrong and we retire live
/// devices over a five-minute outage.
/// </summary>
public class VapidWebPushProviderTests
{
    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Gone)]
    public void Only_404_and_410_mean_the_subscription_is_dead(HttpStatusCode status)
        => VapidWebPushProvider.Classify(status).Outcome.Should().Be(PushSendOutcome.EndpointGone);

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public void Outages_and_rate_limits_are_transient(HttpStatusCode status)
        => VapidWebPushProvider.Classify(status).Outcome.Should().Be(PushSendOutcome.TransientFailure);

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public void A_vapid_key_problem_is_transient_and_never_retires_devices(HttpStatusCode status)
    {
        // Retiring every registration because a key was misconfigured would be
        // unrecoverable -- users would have to re-subscribe by hand. The retry
        // window keeps notifications alive until someone fixes the config.
        var result = VapidWebPushProvider.Classify(status);

        result.Outcome.Should().Be(PushSendOutcome.TransientFailure);
        result.Outcome.Should().NotBe(PushSendOutcome.EndpointGone);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge)]
    public void Our_own_bad_request_is_permanent(HttpStatusCode status)
        => VapidWebPushProvider.Classify(status).Outcome.Should().Be(PushSendOutcome.PermanentFailure);

    [Fact]
    public void An_unknown_5xx_is_still_transient()
        => VapidWebPushProvider.Classify((HttpStatusCode)507).Outcome.Should().Be(PushSendOutcome.TransientFailure);

    [Fact]
    public void Detail_never_leaks_a_credential()
        => VapidWebPushProvider.Classify(HttpStatusCode.Forbidden).Detail
            .Should().NotBeNullOrWhiteSpace().And.NotContain("Key");

    [Theory]
    [InlineData(NotificationPriority.Critical, PushMessageUrgency.High)]
    [InlineData(NotificationPriority.High,     PushMessageUrgency.High)]
    [InlineData(NotificationPriority.Normal,   PushMessageUrgency.Normal)]
    [InlineData(NotificationPriority.Low,      PushMessageUrgency.Low)]
    public void Business_priority_maps_to_web_push_urgency(NotificationPriority priority, PushMessageUrgency expected)
        => VapidWebPushProvider.MapUrgency(priority).Should().Be(expected);

    // ---- payload ----------------------------------------------------------

    private static AppPushMessage Message(IReadOnlyDictionary<string, string?>? data = null) => new(
        Title: "New Shift Assigned",
        Body: "Mumbai Event - today at 6:00 PM",
        NotificationId: Guid.Parse("11111111-2222-3333-4444-555555555555"),
        NotificationType: NotificationTemplateCodes.CrewAssignment,
        DeepLink: "/my-assignments",
        BadgeCount: 3,
        Priority: NotificationPriority.High,
        Sound: "default",
        Data: data);

    [Fact]
    public void The_service_worker_gets_everything_it_needs_to_render_and_route()
    {
        var json = VapidWebPushProvider.Serialize(Message());
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("title").GetString().Should().Be("New Shift Assigned");
        root.GetProperty("body").GetString().Should().Be("Mumbai Event - today at 6:00 PM");
        root.GetProperty("deepLink").GetString().Should().Be("/my-assignments");
        root.GetProperty("badgeCount").GetInt32().Should().Be(3);
        root.GetProperty("notificationType").GetString().Should().Be(NotificationTemplateCodes.CrewAssignment);
        root.GetProperty("notificationId").GetString().Should().Be("11111111-2222-3333-4444-555555555555");
    }

    [Fact]
    public void Oversized_and_empty_extras_are_dropped_to_stay_inside_the_payload_cap()
    {
        // Push services cap the encrypted body; 4KB is the safe assumption, so
        // extras are a courtesy, not a transport for content.
        var data = new Dictionary<string, string?>
        {
            ["EventId"]  = "abc-123",
            ["Blank"]    = "",
            ["Missing"]  = null,
            ["Huge"]     = new string('x', 500)
        };

        var json = VapidWebPushProvider.Serialize(Message(data));
        using var doc = JsonDocument.Parse(json);
        var extras = doc.RootElement.GetProperty("data");

        extras.TryGetProperty("EventId", out _).Should().BeTrue();
        extras.TryGetProperty("Blank",   out _).Should().BeFalse();
        extras.TryGetProperty("Missing", out _).Should().BeFalse();
        extras.TryGetProperty("Huge",    out _).Should().BeFalse();
    }

    [Fact]
    public void A_message_with_no_extras_has_no_data_object()
    {
        using var doc = JsonDocument.Parse(VapidWebPushProvider.Serialize(Message()));

        doc.RootElement.TryGetProperty("data", out _).Should().BeFalse();
    }

    [Fact]
    public void The_payload_stays_comfortably_under_the_four_kilobyte_cap()
    {
        var data = Enumerable.Range(0, 30).ToDictionary(i => $"Key{i}", i => (string?)$"value-{i}");

        var bytes = System.Text.Encoding.UTF8.GetByteCount(VapidWebPushProvider.Serialize(Message(data)));

        bytes.Should().BeLessThan(3_000, "encryption adds overhead on top of the JSON");
    }
}
