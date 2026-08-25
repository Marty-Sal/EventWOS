using EventOpsOracle.Application.Notifications.Abstractions;
using EventOpsOracle.Application.Notifications.Rendering;
using EventOpsOracle.Domain.Entities;
using EventOpsOracle.Domain.Enums;
using EventOpsOracle.Infrastructure.Notifications.Channels;
using FluentAssertions;
using Xunit;

namespace EventOpsOracle.Application.UnitTests.Notifications;

/// <summary>
/// Positional parameter ordering for provider templates. This is the one part of
/// the send path where a bug produces a WRONG message rather than no message --
/// the venue name appearing where the date belongs -- and nobody reports those,
/// they just distrust the system. Hence the explicit ordering and these tests.
/// </summary>
public class WhatsAppTemplateParametersTests
{
    private static NotificationSendContext Context(
        string? providerParams,
        IDictionary<string, string?> data,
        IReadOnlyList<string>? bodyOrder = null)
    {
        var template = new NotificationTemplate(
            "CREW_ASSIGNMENT", NotificationChannel.WhatsApp,
            "Hi {{RecipientName}}, you are assigned to {{EventName}} on {{EventDate}}",
            providerTemplateId: "crew_assignment_v1",
            providerParams: providerParams);

        var notification = new Notification(
            Guid.NewGuid(), "CREW_ASSIGNMENT", NotificationPriority.High, "{}", "key:1");

        notification.AddDelivery(NotificationChannel.WhatsApp, "9876543210", "AiSensy", 1);

        var rendered = new RenderedNotification(
            null, "rendered body", Array.Empty<string>(),
            bodyOrder ?? Array.Empty<string>());

        return new NotificationSendContext(
            notification, notification.Deliveries.First(), template, rendered,
            new Dictionary<string, string?>(data));
    }

    [Fact]
    public void Uses_the_templates_declared_order_not_the_dictionary_order()
    {
        // The data dictionary deliberately lists these in the wrong order.
        var context = Context(
            "RecipientName,EventName,EventDate",
            new Dictionary<string, string?>
            {
                ["EventDate"]     = "24 Aug",
                ["EventName"]     = "Sunburn",
                ["RecipientName"] = "Asha"
            });

        WhatsAppTemplateParameters.Build(context).Should().Equal("Asha", "Sunburn", "24 Aug");
    }

    [Fact]
    public void Tolerates_whitespace_in_the_declared_order()
        => WhatsAppTemplateParameters.Build(Context(
                " RecipientName , EventName ",
                new Dictionary<string, string?> { ["RecipientName"] = "Asha", ["EventName"] = "Sunburn" }))
            .Should().Equal("Asha", "Sunburn");

    [Fact]
    public void A_missing_value_becomes_a_placeholder_rather_than_a_rejected_send()
    {
        // Providers reject empty parameters outright, so an incomplete message is
        // still better than no message -- and the renderer already logged the gap.
        var context = Context(
            "RecipientName,EventName,ShiftName",
            new Dictionary<string, string?> { ["RecipientName"] = "Asha", ["EventName"] = "Sunburn" });

        WhatsAppTemplateParameters.Build(context).Should().Equal("Asha", "Sunburn", "-");
    }

    [Fact]
    public void Blank_values_are_treated_as_missing()
        => WhatsAppTemplateParameters.Build(Context(
                "RecipientName,EventName",
                new Dictionary<string, string?> { ["RecipientName"] = "Asha", ["EventName"] = "   " }))
            .Should().Equal("Asha", "-");

    [Fact]
    public void Falls_back_to_body_token_order_when_no_order_is_declared()
    {
        // Correct whenever the approved provider wording mirrors ours, which is
        // true of the seeded defaults.
        var context = Context(
            providerParams: null,
            data: new Dictionary<string, string?> { ["RecipientName"] = "Asha" },
            bodyOrder: new[] { "Asha", "Sunburn", "24 Aug" });

        WhatsAppTemplateParameters.Build(context).Should().Equal("Asha", "Sunburn", "24 Aug");
    }

    [Fact]
    public void Setting_a_provider_template_bumps_the_version()
    {
        // Deliveries stamp the version, so history shows which wording went out.
        var template = new NotificationTemplate("CREW_ASSIGNMENT", NotificationChannel.WhatsApp, "body");
        var before = template.Version;

        template.SetProviderTemplate("crew_assignment_v2", "RecipientName,EventName", DateTime.UtcNow);

        template.Version.Should().Be(before + 1);
        template.ParameterOrder().Should().Equal("RecipientName", "EventName");
    }
}
