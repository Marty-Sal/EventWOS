using EventWOS.Application.Notifications.Contracts;
using EventWOS.Persistence.Seed;
using FluentAssertions;
using Xunit;

namespace EventWOS.Application.UnitTests.Notifications;

/// <summary>
/// Which codes may be delivered by push.
///
/// This is pinned by tests because it is a security boundary rather than a
/// formatting choice. The exclusion is easy to "tidy up" later by someone who
/// reasonably assumes every notification should push -- and the consequence of
/// getting it wrong is a one-time password sitting in the outbox table and
/// previewed on a lock screen.
/// </summary>
public class PushTemplateSeedingTests
{
    [Fact]
    public void The_password_reset_code_is_never_delivered_by_push()
    {
        // Two reasons, both sufficient on their own: the durable outbox would
        // persist the plaintext code in the database, defeating the hash-only
        // design in OtpRequests; and a lock-screen preview is readable by
        // whoever is holding the phone, which is not necessarily the owner.
        NotificationTemplateSeeder.SupportsPush(NotificationTemplateCodes.PasswordResetOtp)
            .Should().BeFalse();
    }

    [Fact]
    public void The_exclusion_is_case_insensitive()
        // Template codes are compared case-insensitively everywhere else in the
        // platform, so a differently-cased code must not slip past the check.
        => NotificationTemplateSeeder.SupportsPush("password_reset_otp").Should().BeFalse();

    [Fact]
    public void Operational_news_does_support_push()
    {
        // The whole point of the channel: things worth interrupting someone for.
        NotificationTemplateSeeder.SupportsPush(NotificationTemplateCodes.CrewInvitation).Should().BeTrue();
        NotificationTemplateSeeder.SupportsPush(NotificationTemplateCodes.PayrollReleased).Should().BeTrue();
        NotificationTemplateSeeder.SupportsPush(NotificationTemplateCodes.VendorEventInvited).Should().BeTrue();
    }

    [Fact]
    public void Every_seeded_code_except_the_otp_gets_a_push_template()
    {
        var withoutPush = NotificationTemplateSeeder.SeededCodes
            .Where(code => !NotificationTemplateSeeder.SupportsPush(code))
            .ToList();

        // If this list ever grows, it should grow deliberately and with a reason
        // written down -- which is what this assertion forces.
        withoutPush.Should().BeEquivalentTo(new[] { NotificationTemplateCodes.PasswordResetOtp });
    }

    [Fact]
    public void Every_pushable_code_has_a_deep_link()
    {
        // A push whose click goes nowhere useful trains people to ignore pushes.
        foreach (var code in NotificationTemplateSeeder.SeededCodes.Where(NotificationTemplateSeeder.SupportsPush))
        {
            var link = PushDeepLinks.For(code, null);

            link.Should().StartWith("/", $"the deep link for {code} must be site-relative");
            link.Should().NotStartWith("//", $"the deep link for {code} must not be protocol-relative");
        }
    }
}
