using EventOpsOracle.Application.Notifications.Contracts;
using FluentAssertions;
using Xunit;

namespace EventOpsOracle.Application.UnitTests.Notifications;

/// <summary>
/// Deep links decide where a tapped notification lands, and they arrive at the
/// browser inside a payload. So the interesting tests are the hostile ones: a
/// path that escapes OpsOracle turns a notification into an open redirect.
/// </summary>
public class PushDeepLinkTests
{
    [Theory]
    [InlineData(NotificationTemplateCodes.CrewAssignment,        "/my-assignments")]
    [InlineData(NotificationTemplateCodes.ShiftChanged,          "/my-assignments")]
    [InlineData(NotificationTemplateCodes.VendorEventInvited,    "/my-events")]
    [InlineData(NotificationTemplateCodes.CrewAcceptedAssignment,"/vendor-assignments")]
    [InlineData(NotificationTemplateCodes.AttendanceReminder,    "/my-attendance")]
    [InlineData(NotificationTemplateCodes.PaymentApproved,       "/my-payments")]
    [InlineData(NotificationTemplateCodes.PayrollReleased,       "/my-payments")]
    [InlineData(NotificationTemplateCodes.AssignmentPendingApproval, "/manager-approvals")]
    [InlineData(NotificationTemplateCodes.RegistrationPendingApproval, "/approvals/people")]
    public void Codes_land_on_the_screen_that_shows_the_thing(string code, string expected)
        => PushDeepLinks.For(code).Should().Be(expected);

    [Fact]
    public void An_unknown_code_falls_back_to_the_inbox()
        => PushDeepLinks.For("SOMETHING_NEW_NEXT_QUARTER").Should().Be("/notifications");

    [Fact]
    public void Announcements_land_in_the_inbox_where_they_are_read()
        => PushDeepLinks.For(NotificationTemplateCodes.EventAnnouncement).Should().Be("/notifications");

    [Fact]
    public void A_call_site_can_override_the_destination()
    {
        var data = new Dictionary<string, string?> { ["DeepLink"] = "/my-attendance" };

        PushDeepLinks.For(NotificationTemplateCodes.PaymentApproved, data).Should().Be("/my-attendance");
    }

    [Theory]
    [InlineData("https://evil.example/steal")]   // absolute URL
    [InlineData("//evil.example/steal")]         // protocol-relative: the browser treats this as external
    [InlineData("javascript:alert(1)")]          // no scheme of any kind
    [InlineData("/legit\\..\\escape")]           // backslashes
    [InlineData("my-assignments")]               // not rooted
    [InlineData("")]
    public void A_hostile_override_is_ignored_and_the_code_decides(string hostile)
    {
        var data = new Dictionary<string, string?> { ["DeepLink"] = hostile };

        PushDeepLinks.For(NotificationTemplateCodes.PaymentApproved, data)
            .Should().Be("/my-payments", "an override is a convenience, not a trust boundary");
    }

    [Fact]
    public void An_overlong_override_is_ignored()
    {
        var data = new Dictionary<string, string?> { ["DeepLink"] = "/" + new string('a', 400) };

        PushDeepLinks.For(NotificationTemplateCodes.CheckInVerified, data).Should().Be("/my-attendance");
    }

    [Fact]
    public void Sanitize_accepts_ordinary_local_paths()
    {
        PushDeepLinks.TrySanitize("/events", out var path).Should().BeTrue();
        path.Should().Be("/events");

        PushDeepLinks.TrySanitize("  /my-payments  ", out var trimmed).Should().BeTrue();
        trimmed.Should().Be("/my-payments");
    }

    [Fact]
    public void Sanitize_rejects_control_characters()
        => PushDeepLinks.TrySanitize("/my-\npayments", out _).Should().BeFalse();
}
