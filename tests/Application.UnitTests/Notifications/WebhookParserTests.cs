using EventOpsOracle.Application.Notifications.Commands;
using EventOpsOracle.Infrastructure.Notifications.Webhooks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EventOpsOracle.Application.UnitTests.Notifications;

/// <summary>
/// Webhook parsing, using payload shapes as the providers actually send them.
///
/// The recurring theme is that a parser must never throw: providers disable
/// endpoints that keep returning errors, so one malformed batch would cost the
/// entire delivery feed, and every message would silently stay stuck at Accepted.
/// </summary>
public class WebhookParserTests
{
    private static readonly NullLogger Logger = NullLogger.Instance;

    // ── SendGrid ─────────────────────────────────────────────────────────────

    [Fact]
    public void SendGrid_delivered_event_is_correlated_by_our_own_delivery_id()
    {
        var deliveryId = Guid.NewGuid();
        var json = $$"""
            [{
              "event": "delivered",
              "email": "asha@example.com",
              "timestamp": 1787000000,
              "sg_message_id": "abc123.filter0001.16648.5515E0B88.0",
              "deliveryId": "{{deliveryId}}",
              "response": "250 OK"
            }]
            """;

        var events = SendGridWebhookParser.Parse(json, Logger);

        events.Should().HaveCount(1);
        events[0].DeliveryId.Should().Be(deliveryId);
        events[0].Type.Should().Be(ProviderDeliveryEventType.Delivered);

        // The suffix SendGrid appends in flight must be cut, or the fallback
        // match against the stored X-Message-Id would never hit.
        events[0].ProviderMessageId.Should().Be("abc123");
        events[0].OccurredAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1787000000).UtcDateTime);
    }

    [Theory]
    [InlineData("bounce")]
    [InlineData("dropped")]
    [InlineData("blocked")]
    [InlineData("spamreport")]
    public void SendGrid_hard_failures_are_failures(string eventName)
    {
        var events = SendGridWebhookParser.Parse(
            $$"""[{"event":"{{eventName}}","reason":"550 unknown recipient","timestamp":1787000000}]""", Logger);

        events.Should().HaveCount(1);
        events[0].Type.Should().Be(ProviderDeliveryEventType.Failed);
        events[0].Detail.Should().Contain("550");
    }

    [Fact]
    public void SendGrid_deferral_is_not_a_failure()
        // SendGrid is still trying; marking it failed would declare a live
        // message dead.
        => SendGridWebhookParser.Parse("""[{"event":"deferred","timestamp":1787000000}]""", Logger)
            .Single().Type.Should().Be(ProviderDeliveryEventType.Deferred);

    [Fact]
    public void SendGrid_tracking_only_events_are_dropped()
        // Clicks and unsubscribes say nothing about whether it was delivered.
        => SendGridWebhookParser.Parse(
                """[{"event":"click","timestamp":1}, {"event":"unsubscribe","timestamp":2}]""", Logger)
            .Should().BeEmpty();

    [Fact]
    public void SendGrid_batch_of_mixed_events_is_parsed_together()
    {
        var events = SendGridWebhookParser.Parse("""
            [{"event":"delivered","timestamp":1787000000},
             {"event":"open","timestamp":1787000100},
             {"event":"bounce","reason":"blocked","timestamp":1787000200}]
            """, Logger);

        events.Select(e => e.Type).Should().Equal(
            ProviderDeliveryEventType.Delivered,
            ProviderDeliveryEventType.Read,
            ProviderDeliveryEventType.Failed);
    }

    // ── Meta WhatsApp ────────────────────────────────────────────────────────

    [Fact]
    public void Meta_status_events_are_read_from_the_nested_payload()
    {
        var json = """
            {
              "object": "whatsapp_business_account",
              "entry": [{
                "id": "123",
                "changes": [{
                  "field": "messages",
                  "value": {
                    "messaging_product": "whatsapp",
                    "statuses": [
                      {"id":"wamid.AAA","status":"delivered","timestamp":"1787000000","recipient_id":"919876543210"},
                      {"id":"wamid.BBB","status":"read","timestamp":"1787000100","recipient_id":"919876543210"}
                    ]
                  }
                }]
              }]
            }
            """;

        var events = MetaWhatsAppWebhookParser.Parse(json, Logger);

        events.Should().HaveCount(2);
        events[0].ProviderMessageId.Should().Be("wamid.AAA");
        events[0].Type.Should().Be(ProviderDeliveryEventType.Delivered);
        events[1].Type.Should().Be(ProviderDeliveryEventType.Read);
    }

    [Fact]
    public void Meta_failure_carries_the_provider_error_so_the_cause_is_recorded()
    {
        var json = """
            {"entry":[{"changes":[{"value":{"statuses":[{
              "id":"wamid.CCC","status":"failed","timestamp":"1787000000",
              "errors":[{"code":131026,"title":"Message undeliverable"}]
            }]}}]}]}
            """;

        var evt = MetaWhatsAppWebhookParser.Parse(json, Logger).Single();

        evt.Type.Should().Be(ProviderDeliveryEventType.Failed);
        evt.Detail.Should().Contain("131026").And.Contain("Message undeliverable");
    }

    [Fact]
    public void Meta_sent_status_is_ignored_as_it_repeats_the_send_call()
        => MetaWhatsAppWebhookParser.Parse(
                """{"entry":[{"changes":[{"value":{"statuses":[{"id":"wamid.D","status":"sent","timestamp":"1"}]}}]}]}""", Logger)
            .Should().BeEmpty();

    [Fact]
    public void Meta_inbound_user_messages_are_not_mistaken_for_statuses()
    {
        // Replies from crew arrive on the same webhook and have no statuses[].
        var json = """
            {"entry":[{"changes":[{"value":{"messages":[{"from":"919876543210","text":{"body":"ok"}}]}}]}]}
            """;

        MetaWhatsAppWebhookParser.Parse(json, Logger).Should().BeEmpty();
    }

    // ── AiSensy ──────────────────────────────────────────────────────────────

    [Fact]
    public void AiSensy_delivery_id_is_found_in_attributes()
    {
        var deliveryId = Guid.NewGuid();
        var json = """{"status":"delivered","timestamp":1787000000,"attributes":{"deliveryId":"ID","code":"CREW_ASSIGNMENT"}}"""
            .Replace("ID", deliveryId.ToString());

        AiSensyWebhookParser.Parse(json, Logger).Single().DeliveryId.Should().Be(deliveryId);
    }

    [Fact]
    public void AiSensy_delivery_id_is_also_found_in_tags()
    {
        // Their payload shape varies by account configuration, and this is the
        // only dependable correlation handle -- their send API returns no id.
        var deliveryId = Guid.NewGuid();
        // Plain concatenation: brace-heavy JSON plus a raw interpolated literal is
        // more trouble than it is worth here.
        var json = "{\"status\":\"read\",\"tags\":[\"source:OpsOracle\",\"delivery:" + deliveryId + "\"]}";

        var evt = AiSensyWebhookParser.Parse(json, Logger).Single();

        evt.DeliveryId.Should().Be(deliveryId);
        evt.Type.Should().Be(ProviderDeliveryEventType.Read);
    }

    [Fact]
    public void AiSensy_accepts_both_a_single_object_and_a_batch()
    {
        AiSensyWebhookParser.Parse("""{"status":"delivered"}""", Logger).Should().HaveCount(1);
        AiSensyWebhookParser.Parse("""[{"status":"delivered"},{"status":"failed"}]""", Logger).Should().HaveCount(2);
    }

    [Fact]
    public void AiSensy_iso_timestamps_are_understood()
        => AiSensyWebhookParser.Parse(
                """{"status":"delivered","eventTime":"2026-08-24T10:30:00Z"}""", Logger)
            .Single().OccurredAt.Should().Be(new DateTime(2026, 8, 24, 10, 30, 0, DateTimeKind.Utc));

    // ── Robustness ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{\"unexpected\":\"shape\"}")]
    [InlineData("[]")]
    public void No_parser_ever_throws_on_junk(string payload)
    {
        // A parser that throws becomes a 500, and a provider that sees repeated
        // 500s disables the endpoint -- taking the whole delivery feed with it.
        SendGridWebhookParser.Parse(payload, Logger).Should().NotBeNull();
        MetaWhatsAppWebhookParser.Parse(payload, Logger).Should().NotBeNull();
        AiSensyWebhookParser.Parse(payload, Logger).Should().NotBeNull();
    }
}
