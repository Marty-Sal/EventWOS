using System.Net.Http.Json;
using System.Text.Json;
using EventOpsOracle.Application.Notifications.Abstractions;
using EventOpsOracle.Application.Notifications.Services;
using EventOpsOracle.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EventOpsOracle.Infrastructure.Notifications.Channels;

/// <summary>
/// Meta WhatsApp Cloud API sender.
///
/// Sends an approved template when the notification template names one, and only
/// falls back to free-form text when it does not. That order matters: outside a
/// 24-hour service window Meta silently refuses free-form text, so a system that
/// defaults to plain text appears to work in testing (where you have just
/// messaged the number) and then quietly reaches nobody in production.
///
/// Preferred over AiSensy for correlation: the response carries a real
/// wamid message id, which is what delivery and read webhooks arrive against.
/// </summary>
public sealed class MetaWhatsAppSender : INotificationChannelSender
{
    private readonly HttpClient _http;
    private readonly WhatsAppOptions _options;
    private readonly ILogger<MetaWhatsAppSender> _logger;

    public MetaWhatsAppSender(HttpClient http, IOptions<WhatsAppOptions> options, ILogger<MetaWhatsAppSender> logger)
    {
        _http    = http;
        _options = options.Value;
        _logger  = logger;
    }

    public NotificationChannel Channel => NotificationChannel.WhatsApp;

    public string ProviderName => "MetaCloud";

    public bool IsConfigured => _options.IsMeta && _options.Meta.IsConfigured;

    public async Task<ChannelSendResult> SendAsync(NotificationSendContext context, CancellationToken ct = default)
    {
        var to = WhatsAppNumber.Normalize(context.Destination, _options.DefaultCountryCode);
        if (to is null)
        {
            // Not a failure worth retrying or alerting on -- the number stored on
            // the user is unusable, which is a data problem, not a send problem.
            return ChannelSendResult.Skip($"Unusable WhatsApp number '{context.Destination}'");
        }

        var meta = _options.Meta;
        var body = BuildPayload(context, to, meta);

        using var response = await _http.PostAsJsonAsync(
            $"{meta.GraphVersion}/{meta.PhoneNumberId}/messages", body, ct);

        var raw = await response.Content.ReadAsStringAsync(ct);

        if (response.IsSuccessStatusCode)
        {
            var messageId = ExtractMessageId(raw);

            // No message id means no webhook correlation later, so it is worth
            // noticing rather than assuming.
            if (messageId is null)
                _logger.LogWarning("Meta accepted delivery {DeliveryId} but returned no message id", context.Delivery.Id);

            return ChannelSendResult.Accepted(messageId, detail: "Accepted by Meta Cloud API");
        }

        var (code, message) = ExtractError(raw);
        var outcome = MetaWhatsAppErrorPolicy.Classify(response.StatusCode, code);

        // The provider's own words, truncated -- enough to diagnose, not enough
        // to dump a payload into the database.
        var detail = $"Meta {(int)response.StatusCode} code={code?.ToString() ?? "?"}: {Truncate(message ?? raw, 300)}";

        _logger.LogWarning(
            "Meta WhatsApp send failed for delivery {DeliveryId}: {Status} code={Code} -> {Outcome}",
            context.Delivery.Id, (int)response.StatusCode, code, outcome);

        return outcome == ChannelSendOutcome.PermanentFailure
            ? ChannelSendResult.Permanent(detail)
            : ChannelSendResult.Transient(detail);
    }

    private static object BuildPayload(NotificationSendContext context, string to, MetaWhatsAppOptions meta)
    {
        var templateName = context.Template.ProviderTemplateId;

        if (string.IsNullOrWhiteSpace(templateName))
        {
            // Only valid inside the 24-hour service window. Templates are seeded
            // inactive precisely so this path is not the default in production.
            return new
            {
                messaging_product = "whatsapp",
                to,
                type = "text",
                text = new { body = context.Message.Body, preview_url = false }
            };
        }

        var parameters = WhatsAppTemplateParameters.Build(context);

        return new
        {
            messaging_product = "whatsapp",
            to,
            type = "template",
            template = new
            {
                name = templateName,
                language = new { code = meta.TemplateLanguage },
                components = parameters.Count == 0
                    ? Array.Empty<object>()
                    : new object[]
                    {
                        new
                        {
                            type = "body",
                            parameters = parameters.Select(p => new { type = "text", text = p }).ToArray()
                        }
                    }
            }
        };
    }

    /// <summary>Pulls the wamid out of { "messages": [ { "id": "wamid...." } ] }.</summary>
    private static string? ExtractMessageId(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("messages", out var messages) &&
                messages.ValueKind == JsonValueKind.Array &&
                messages.GetArrayLength() > 0 &&
                messages[0].TryGetProperty("id", out var id))
            {
                return id.GetString();
            }
        }
        catch (JsonException) { /* fall through -- shape changed, not fatal */ }

        return null;
    }

    private static (int? Code, string? Message) ExtractError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                var code = error.TryGetProperty("code", out var c) && c.TryGetInt32(out var parsed) ? parsed : (int?)null;
                var msg  = error.TryGetProperty("message", out var m) ? m.GetString() : null;
                return (code, msg);
            }
        }
        catch (JsonException) { }

        return (null, null);
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "...";
}
