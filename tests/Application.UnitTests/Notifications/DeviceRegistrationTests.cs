using EventWOS.Domain.Entities;
using EventWOS.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace EventWOS.Application.UnitTests.Notifications;

/// <summary>
/// Lifecycle rules for a push subscription. These matter more than they look:
/// a subscription is cache-like data the browser replaces at will, so the two
/// ways to get this wrong are inserting a second row for a device that already
/// exists, and retrying an endpoint the push service has already declared dead.
/// </summary>
public class DeviceRegistrationTests
{
    private static readonly DateTime Now = new(2026, 8, 24, 5, 0, 0, DateTimeKind.Utc);
    private static readonly Guid     User = Guid.NewGuid();

    private static DeviceRegistration WebPush() => DeviceRegistration.ForWebPush(
        User, "https://fcm.googleapis.com/fcm/send/abc123", "p256dh-key", "auth-secret", Now,
        deviceId: "device-1", platform: "Android", userAgent: "Mozilla/5.0 (Android)");

    [Fact]
    public void A_web_push_registration_starts_active_and_seen()
    {
        var reg = WebPush();

        reg.Provider.Should().Be(PushProvider.WebPush);
        reg.IsActive.Should().BeTrue();
        reg.LastSeenAt.Should().Be(Now);
        reg.Endpoint.Should().Be("https://fcm.googleapis.com/fcm/send/abc123");
        reg.PushToken.Should().BeNull("a Web Push row is addressed by endpoint, not by an FCM token");
    }

    [Fact]
    public void Web_push_requires_both_encryption_keys()
    {
        // Without these a push can wake the service worker but carries no payload,
        // so a row missing them is useless rather than merely incomplete.
        var act = () => DeviceRegistration.ForWebPush(User, "https://push/x", "", "auth", Now);
        act.Should().Throw<ArgumentException>();

        var act2 = () => DeviceRegistration.ForWebPush(User, "https://push/x", "p256dh", "  ", Now);
        act2.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void An_fcm_registration_carries_a_token_and_no_endpoint()
    {
        var reg = DeviceRegistration.ForFcm(User, "fcm-token-xyz", Now);

        reg.Provider.Should().Be(PushProvider.Fcm);
        reg.PushToken.Should().Be("fcm-token-xyz");
        reg.Endpoint.Should().BeNull();
    }

    [Fact]
    public void A_registration_always_has_an_owner()
    {
        var act = () => DeviceRegistration.ForWebPush(Guid.Empty, "https://push/x", "k", "a", Now);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Gone_endpoints_are_deactivated_with_a_reason_not_deleted()
    {
        var reg   = WebPush();
        var later = Now.AddHours(2);

        reg.Deactivate("410 Gone", later);

        reg.IsActive.Should().BeFalse();
        reg.DeactivatedAt.Should().Be(later);
        reg.DeactivationReason.Should().Be("410 Gone");
        reg.IsDeleted.Should().BeFalse("the row stays for audit -- we did try to reach this person");
    }

    [Fact]
    public void Deactivation_is_idempotent_and_keeps_the_first_reason()
    {
        var reg = WebPush();
        reg.Deactivate("410 Gone", Now.AddHours(1));
        reg.Deactivate("user disabled", Now.AddHours(5));

        reg.DeactivationReason.Should().Be("410 Gone", "the first reason is the one that explains what happened");
        reg.DeactivatedAt.Should().Be(Now.AddHours(1));
    }

    [Fact]
    public void Re_subscribing_reactivates_the_same_row()
    {
        // The unique index on endpoint means the alternative is a constraint
        // violation, so Touch has to be able to bring a retired row back.
        var reg = WebPush();
        reg.Deactivate("410 Gone", Now.AddHours(1));

        reg.Touch(Now.AddDays(1), platform: "Android", userAgent: "Mozilla/5.0 (Android 15)");

        reg.IsActive.Should().BeTrue();
        reg.DeactivatedAt.Should().BeNull();
        reg.DeactivationReason.Should().BeNull();
        reg.LastSeenAt.Should().Be(Now.AddDays(1));
        reg.UserAgent.Should().Be("Mozilla/5.0 (Android 15)");
    }

    [Fact]
    public void A_bare_heartbeat_does_not_blank_what_we_already_knew()
    {
        var reg = WebPush();

        reg.Touch(Now.AddMinutes(30));

        reg.Platform.Should().Be("Android");
        reg.DeviceId.Should().Be("device-1");
        reg.UserAgent.Should().NotBeNull();
    }

    [Fact]
    public void Transient_failures_count_up_and_a_success_clears_them()
    {
        var reg = WebPush();

        reg.RecordTransientFailure(Now.AddMinutes(1));
        reg.RecordTransientFailure(Now.AddMinutes(2));
        reg.ConsecutiveFailures.Should().Be(2);
        reg.IsActive.Should().BeTrue("a flaky night is not a dead subscription");

        reg.RecordSuccess(Now.AddMinutes(3));
        reg.ConsecutiveFailures.Should().Be(0);
        reg.LastSuccessAt.Should().Be(Now.AddMinutes(3));
    }

    [Fact]
    public void Rotated_browser_keys_are_accepted()
    {
        var reg = WebPush();

        reg.RotateKeys("new-p256dh", "new-auth", Now.AddDays(3));

        reg.P256dhKey.Should().Be("new-p256dh");
        reg.AuthSecret.Should().Be("new-auth");
        reg.Endpoint.Should().Be("https://fcm.googleapis.com/fcm/send/abc123", "same subscription, new keys");
    }
}
