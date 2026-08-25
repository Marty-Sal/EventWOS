using EventOpsOracle.Application.Notifications.Contracts;
using EventOpsOracle.Domain.Enums;
using EventOpsOracle.Persistence.Seed;
using FluentAssertions;
using Xunit;

namespace EventOpsOracle.Application.UnitTests.Notifications;

/// <summary>
/// Push has to be listed in NotificationPolicy to happen at all.
///
/// This is the gap that made the entire push feature inert in production while
/// every individual part of it worked: entity, API, subscription, service
/// worker, provider, sender and templates were all correct and live, but no
/// notification type ever ASKED for the push channel, so no push delivery row
/// was ever created. Nothing failed and nothing logged -- it simply never
/// happened.
///
/// A new template code is easy to add without touching this table, and it will
/// silently not push. These tests make that omission fail the build instead.
/// </summary>
public class PushChannelPolicyTests
{
    /// <summary>
    /// Codes deliberately NOT pushed, each for a stated reason. Anything else
    /// missing push is a bug, not a choice.
    /// </summary>
    public static readonly string[] DeliberatelyNotPushed =
    {
        // Delivered by a channel the person controls, and never through the
        // durable outbox at all -- see the OTP decision.
        NotificationTemplateCodes.PasswordResetOtp,

        // A receipt for something the person did on that same phone seconds
        // earlier.
        NotificationTemplateCodes.CheckInVerified,

        // Low-value housekeeping for whoever manages the person.
        NotificationTemplateCodes.ProfileCompleted
    };

    [Theory]
    [InlineData(NotificationTemplateCodes.CrewAssignmentRejected)]
    [InlineData(NotificationTemplateCodes.CrewAssignmentApproved)]
    [InlineData(NotificationTemplateCodes.CrewAssignment)]
    [InlineData(NotificationTemplateCodes.EventCancelled)]
    [InlineData(NotificationTemplateCodes.PayrollReleased)]
    [InlineData(NotificationTemplateCodes.VendorEventInvited)]
    public void The_news_worth_interrupting_someone_for_includes_push(string code)
        => NotificationPolicy.DefaultChannels(code).Should().Contain(NotificationChannel.Push);

    [Fact]
    public void Every_seeded_code_either_pushes_or_is_on_the_deliberate_list()
    {
        var missing = NotificationTemplateSeeder.SeededCodes
            .Where(code => !DeliberatelyNotPushed.Contains(code, StringComparer.OrdinalIgnoreCase))
            .Where(code => !NotificationPolicy.DefaultChannels(code).Contains(NotificationChannel.Push))
            .ToList();

        missing.Should().BeEmpty(
            "a seeded code with no push in its policy will never reach a phone, and nothing will report it");
    }

    [Fact]
    public void Every_seeded_code_has_an_explicit_policy_entry()
    {
        // The unknown-code fallback is in-app only. That is a safe default for a
        // code shipped mid-deploy, but a permanent silent downgrade for one that
        // was simply forgotten -- which is exactly what happened to the six
        // review/approval codes added after this table was written.
        var unknown = NotificationTemplateSeeder.SeededCodes
            .Where(code => !NotificationPolicy.IsKnown(code))
            .ToList();

        unknown.Should().BeEmpty("these fall back to in-app only and quietly never leave the building");
    }

    [Fact]
    public void The_password_reset_code_is_never_pushed()
        // Belt and braces with PushTemplateSeedingTests: no template AND no
        // policy entry. Either alone would stop it; both is deliberate.
        => NotificationPolicy.DefaultChannels(NotificationTemplateCodes.PasswordResetOtp)
            .Should().NotContain(NotificationChannel.Push);

    [Fact]
    public void In_app_survives_everywhere_push_was_added()
    {
        // Push is additive. The bell must keep working for someone who has never
        // enabled notifications, which is most people.
        foreach (var code in NotificationTemplateSeeder.SeededCodes)
        {
            if (code == NotificationTemplateCodes.PasswordResetOtp) continue;

            NotificationPolicy.DefaultChannels(code)
                .Should().Contain(NotificationChannel.InApp, $"{code} must still reach the in-app inbox");
        }
    }
}
