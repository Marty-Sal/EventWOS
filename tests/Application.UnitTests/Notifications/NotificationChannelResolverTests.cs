using EventWOS.Application.Notifications.Abstractions;
using EventWOS.Application.Notifications.Contracts;
using EventWOS.Application.Notifications.Rendering;
using EventWOS.Application.Notifications.Services;
using EventWOS.Domain.Entities;
using EventWOS.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace EventWOS.Application.UnitTests.Notifications;

/// <summary>
/// Covers the four gates that decide where a notification goes. These rules are
/// what stop the system generating failures it can never resolve -- crew with no
/// email address, channels whose provider was never configured, templates an
/// admin switched off.
/// </summary>
public class NotificationChannelResolverTests
{
    private static NotificationRecipient Crew(string? email = null, string? mobile = "9876543210")
        => new(Guid.NewGuid(), "Asha Kumar", email, mobile);

    private static Dictionary<NotificationChannel, NotificationTemplate> Templates(params NotificationChannel[] channels)
        => channels.ToDictionary(c => c, c => new NotificationTemplate("CREW_ASSIGNMENT", c, "body {{EventName}}", "subject"));

    private sealed class FakeSender : INotificationChannelSender
    {
        public FakeSender(NotificationChannel channel, bool configured = true, string provider = "Fake")
        { Channel = channel; IsConfigured = configured; ProviderName = provider; }

        public NotificationChannel Channel { get; }
        public string ProviderName { get; }
        public bool IsConfigured { get; }
        public Task<ChannelSendResult> SendAsync(NotificationSendContext context, CancellationToken ct = default)
            => Task.FromResult(ChannelSendResult.Accepted());
    }

    private static NotificationChannelResolver Resolver(params INotificationChannelSender[] senders)
        => new(senders);

    [Fact]
    public void Resolves_policy_channels_when_everything_is_available()
    {
        var resolver = Resolver(
            new FakeSender(NotificationChannel.InApp),
            new FakeSender(NotificationChannel.WhatsApp));

        var resolved = resolver.Resolve(
            NotificationTemplateCodes.CrewAssignment,
            Crew(),
            Templates(NotificationChannel.InApp, NotificationChannel.WhatsApp),
            requestedChannels: null);

        resolved.Select(r => r.Channel).Should().BeEquivalentTo(new[] { NotificationChannel.InApp, NotificationChannel.WhatsApp });
    }

    [Fact]
    public void Skips_a_channel_whose_template_is_inactive_or_absent()
    {
        // How an admin turns a channel off for one notification type, no deploy.
        var resolver = Resolver(
            new FakeSender(NotificationChannel.InApp),
            new FakeSender(NotificationChannel.WhatsApp));

        var resolved = resolver.Resolve(
            NotificationTemplateCodes.CrewAssignment, Crew(),
            Templates(NotificationChannel.InApp), null);

        resolved.Should().HaveCount(1);
        resolved.Single().Channel.Should().Be(NotificationChannel.InApp);
    }

    [Fact]
    public void Skips_a_channel_whose_provider_is_not_configured()
    {
        // A missing AiSensy key degrades to in-app instead of queueing messages
        // that could only ever fail.
        var resolver = Resolver(
            new FakeSender(NotificationChannel.InApp),
            new FakeSender(NotificationChannel.WhatsApp, configured: false));

        var resolved = resolver.Resolve(
            NotificationTemplateCodes.CrewAssignment, Crew(),
            Templates(NotificationChannel.InApp, NotificationChannel.WhatsApp), null);

        resolved.Select(r => r.Channel).Should().Equal(NotificationChannel.InApp);
    }

    [Fact]
    public void Skips_email_for_a_recipient_with_no_email_address()
    {
        var resolver = Resolver(
            new FakeSender(NotificationChannel.InApp),
            new FakeSender(NotificationChannel.Email));

        var resolved = resolver.Resolve(
            NotificationTemplateCodes.AccountApproved,
            Crew(email: null),
            Templates(NotificationChannel.InApp, NotificationChannel.Email),
            null);

        resolved.Should().NotContain(r => r.Channel == NotificationChannel.Email);
    }

    [Fact]
    public void Skips_whatsapp_for_a_recipient_with_no_mobile_number()
    {
        var resolver = Resolver(new FakeSender(NotificationChannel.WhatsApp));

        var resolved = resolver.Resolve(
            NotificationTemplateCodes.CrewAssignment,
            Crew(mobile: null),
            Templates(NotificationChannel.WhatsApp),
            null);

        resolved.Should().BeEmpty();
    }

    [Fact]
    public void In_app_needs_no_contact_details()
    {
        // It is delivered inside our own system, so a user with neither email nor
        // mobile still gets the notification in their list.
        var resolver = Resolver(new FakeSender(NotificationChannel.InApp));

        var resolved = resolver.Resolve(
            NotificationTemplateCodes.CrewAssignment,
            new NotificationRecipient(Guid.NewGuid(), "No Contact", null, null),
            Templates(NotificationChannel.InApp),
            null);

        resolved.Should().HaveCount(1);
        resolved.Single().Destination.Should().BeNull();
    }

    [Fact]
    public void Destination_is_snapshotted_from_the_recipient()
    {
        // Recorded per delivery so history still shows where a message went
        // after the user changes their number.
        var resolver = Resolver(new FakeSender(NotificationChannel.WhatsApp, provider: "AiSensy"));

        var resolved = resolver.Resolve(
            NotificationTemplateCodes.CrewAssignment, Crew(mobile: " 9876543210 "),
            Templates(NotificationChannel.WhatsApp), null);

        resolved.Single().Destination.Should().Be("9876543210");
        resolved.Single().ProviderName.Should().Be("AiSensy");
    }

    [Fact]
    public void An_explicit_channel_override_wins_over_policy()
    {
        var resolver = Resolver(
            new FakeSender(NotificationChannel.InApp),
            new FakeSender(NotificationChannel.Email));

        var resolved = resolver.Resolve(
            NotificationTemplateCodes.CrewAssignment,   // policy: in-app + WhatsApp
            Crew(email: "asha@example.com"),
            Templates(NotificationChannel.InApp, NotificationChannel.Email),
            requestedChannels: new[] { NotificationChannel.Email });

        resolved.Select(r => r.Channel).Should().Equal(NotificationChannel.Email);
    }

    [Fact]
    public void Later_registration_wins_when_two_senders_serve_the_same_channel()
    {
        // How config picks between AiSensy and Meta for WhatsApp.
        var resolver = Resolver(
            new FakeSender(NotificationChannel.WhatsApp, provider: "MetaCloud"),
            new FakeSender(NotificationChannel.WhatsApp, provider: "AiSensy"));

        var resolved = resolver.Resolve(
            NotificationTemplateCodes.CrewAssignment, Crew(),
            Templates(NotificationChannel.WhatsApp), null);

        resolved.Single().ProviderName.Should().Be("AiSensy");
    }
}
