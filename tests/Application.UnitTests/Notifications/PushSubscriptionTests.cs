using EventOpsOracle.Application.Notifications.Commands;
using EventOpsOracle.Application.Notifications.Queries;
using EventOpsOracle.Domain.Entities;
using FluentAssertions;
using Xunit;

namespace EventOpsOracle.Application.UnitTests.Notifications;

/// <summary>
/// Subscription validation, the shared-device case, and the device label.
///
/// The shared-device test is the one that matters most: crew hand phones around,
/// and a push endpoint identifies a browser rather than a person. Getting it wrong
/// means one crew member's shift notifications ringing on a device someone else
/// is now holding.
/// </summary>
public class PushSubscriptionTests
{
    private const string GoodEndpoint = "https://fcm.googleapis.com/fcm/send/dGhpcy1pcy1hLWZha2UtZW5kcG9pbnQ";
    private const string GoodP256dh   = "BJIY0k-cOsJA196OQtrb8S1t15BFhBuMQyTJCGnJvkLtUetpy4Yi81OQu-2XBtOk1Xs7Y5UnrwUnVdjQz8nXNSM";
    private const string GoodAuth     = "c2FsdHNhbHRzYWx0c2FsdA";

    private static RegisterPushSubscriptionCommand Command(
        string? endpoint = null, string? p256dh = null, string? auth = null, Guid? userId = null)
        => new(userId ?? Guid.NewGuid(), endpoint ?? GoodEndpoint, p256dh ?? GoodP256dh, auth ?? GoodAuth);

    [Fact]
    public void A_well_formed_subscription_passes()
        => RegisterPushSubscriptionHandler.Validate(Command()).Should().BeNull();

    [Fact]
    public void An_anonymous_subscription_is_rejected()
        => RegisterPushSubscriptionHandler.Validate(Command(userId: Guid.Empty))
            .Should().NotBeNull();

    [Theory]
    [InlineData("http://fcm.googleapis.com/fcm/send/abc")] // plaintext
    [InlineData("ftp://example.com/push")]
    [InlineData("/relative/path")]
    [InlineData("not a url")]
    [InlineData("")]
    public void Only_absolute_https_endpoints_are_accepted(string endpoint)
    {
        // A junk row would be POSTed to on every single notification for that
        // user, so the door closes here rather than in the sender.
        RegisterPushSubscriptionHandler.Validate(Command(endpoint: endpoint))
            .Should().NotBeNull();
    }

    [Fact]
    public void An_overlong_endpoint_is_rejected()
    {
        var endpoint = "https://push.example/" + new string('a', DeviceRegistration.MaxEndpointLength);

        RegisterPushSubscriptionHandler.Validate(Command(endpoint: endpoint)).Should().NotBeNull();
    }

    [Theory]
    [InlineData("", GoodAuth)]
    [InlineData("too-short", GoodAuth)]
    [InlineData(GoodP256dh, "")]
    [InlineData(GoodP256dh, "short")]
    public void Missing_or_malformed_encryption_keys_are_rejected(string p256dh, string auth)
        // Without both keys a push can wake the service worker but carries no
        // content, which would show the user an empty notification.
        => RegisterPushSubscriptionHandler.Validate(Command(p256dh: p256dh, auth: auth))
            .Should().NotBeNull();

    // ---- the shared-device rule -------------------------------------------

    [Fact]
    public void Reassigning_a_shared_browser_moves_the_subscription_to_the_new_signin()
    {
        var first  = Guid.NewGuid();
        var second = Guid.NewGuid();
        var now    = DateTime.UtcNow;

        var registration = DeviceRegistration.ForWebPush(first, GoodEndpoint, GoodP256dh, GoodAuth, now);
        registration.RecordSuccess(now);

        registration.ReassignTo(second, now.AddMinutes(5));

        registration.UserId.Should().Be(second, "the previous holder must stop being reachable on this device");
        registration.IsActive.Should().BeTrue();
        registration.LastSuccessAt.Should().BeNull("the success history described a different person's device use");
        registration.ConsecutiveFailures.Should().Be(0);
    }

    [Fact]
    public void Reassigning_revives_a_retired_row()
    {
        var now = DateTime.UtcNow;
        var registration = DeviceRegistration.ForWebPush(Guid.NewGuid(), GoodEndpoint, GoodP256dh, GoodAuth, now);
        registration.Deactivate("410 Gone", now);

        registration.ReassignTo(Guid.NewGuid(), now.AddHours(1));

        registration.IsActive.Should().BeTrue();
        registration.DeactivationReason.Should().BeNull();
    }

    [Fact]
    public void Reassigning_to_nobody_is_refused()
    {
        var registration = DeviceRegistration.ForWebPush(
            Guid.NewGuid(), GoodEndpoint, GoodP256dh, GoodAuth, DateTime.UtcNow);

        var act = () => registration.ReassignTo(Guid.Empty, DateTime.UtcNow);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_browser_that_rotates_its_keys_keeps_its_row()
    {
        var now = DateTime.UtcNow;
        var registration = DeviceRegistration.ForWebPush(Guid.NewGuid(), GoodEndpoint, GoodP256dh, GoodAuth, now);

        registration.RotateKeys("B" + new string('x', 80), "newauthsecretvalue", now.AddDays(1));

        registration.AuthSecret.Should().Be("newauthsecretvalue");
        registration.Endpoint.Should().Be(GoodEndpoint, "the endpoint is the identity; only the keys changed");
    }

    // ---- device labels ----------------------------------------------------

    [Theory]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 17_0) AppleWebKit/605.1.15 Version/17.0 Safari/604.1", "Safari on iPhone")]
    [InlineData("Mozilla/5.0 (Linux; Android 14) AppleWebKit/537.36 Chrome/120.0 Mobile Safari/537.36", "Chrome on Android")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0 Safari/537.36 Edg/120.0", "Edge on Windows")]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) Gecko/20100101 Firefox/121.0", "Firefox on Mac")]
    public void A_device_label_is_recognisable_without_being_a_fingerprint(string agent, string expected)
        => GetMyPushDevicesHandler.DescribeDevice(null, agent).Should().Be(expected);

    [Fact]
    public void An_unknown_agent_still_gets_a_label()
        => GetMyPushDevicesHandler.DescribeDevice(null, null).Should().Be("Unknown device");

    [Fact]
    public void A_client_supplied_platform_is_used_when_the_agent_says_nothing()
        => GetMyPushDevicesHandler.DescribeDevice("Android", "some-embedded-webview").Should().Be("Android");
}
