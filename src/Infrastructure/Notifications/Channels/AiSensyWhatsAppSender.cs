using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EventOpsOracle.Application.Notifications.Abstractions;
using EventOpsOracle.Application.Notifications.Services;
using EventOpsOracle.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EventOpsOracle.Infrastructure.Notifications.Channels;

/// <summary>
/// AiSensy sender. AiSensy wraps Meta and sends by CAMPAIGN name rather than
/// template name, so every notification code needs a campaign created there and
/// recorded in the template's ProviderTemplateId.
///
/// Two consequences worth knowing, both handled here:
///
///  * There is no free-form path. A template with no campaign name cannot be
///    sent at all, so it is skipped with a clear reason instead of failing
///    repeatedly against an endpoint that will never accept it.
///  * The API returns no provider message id. Delivery webhooks therefore cannot
///    be correlated by id, so the delivery's own id is passed as a tag/attribute
///    and used as the correlation key on the way back. Without that, a delivery
///    would sit at Accepted forever with no way to learn what happened to it.
/// </summary>
public sealed class AiSensyWhatsAppSender : INotificationChannelSender
{
    private readonly HttpClient _http;
    private readonly WhatsAppOptions _options;
    private readonly ILogger<AiSensyWhatsAppSender> _logger;

    public AiSensyWhatsAppSender(HttpClient http, IOptions<WhatsAppOptions> options, ILogger<AiSensyWhatsAppSender> logger)
    {
        _http    = http;
        _options = options.Value;
        _logger  = logger;
    }

    public NotificationChannel Channel => NotificationChannel.WhatsApp;

    public string ProviderName => "AiSensy";

    public bool IsConfigured => _options.IsAiSensy && _options.AiSensy.IsConfigured;

    public async Task<ChannelSendResult> SendAsync(NotificationSendContext context, CancellationToken ct = default)
    {
        var to = WhatsAppNumber.Normalize(context.Destination, _options.DefaultCountryCode);
        if (to is null)
            return ChannelSendResult.Skip($"Unusable WhatsApp number '{context.Destination}'");

        var campaign = context.Template.ProviderTemplateId ?? _options.AiSensy.DefaultCampaign;
        if (string.IsNullOrWhiteSpace(campaign))
        {
            // Deliberately a skip, not a failure: the wording exists, the campaign
            // just has not been created and approved yet. Failing would bury it in
            // the same bucket as real provider errors.
            _logger.LogWarning(
                "No AiSensy campaign configured for template {TemplateCode}; WhatsApp delivery {DeliveryId} skipped",
                context.Template.Code, context.Delivery.Id);

            return ChannelSendResult.Skip($"No AiSensy campaign for {context.Template.Code}");
        }

        var parameters = WhatsAppTemplateParameters.Build(context);

        var payload = new
        {
            apiKey       = _options.AiSensy.ApiKey,
            campaignName = campaign,
            destination  = to,
            userName     = context.Notification.RecipientUserId.ToString(),
            // AiSensy echoes tags/attributes on its webhooks. This is the only
            // correlation handle available, since the send response has no id.
            source       = "OpsOracle",
            tags         = new[] { $"delivery:{context.Delivery.Id}" },
            attributes   = new Dictionary<string, string>
            {
                ["deliveryId"]     = context.Delivery.Id.ToString(),
                ["notificationId"] = context.Notification.Id.ToString(),
                ["code"]           = context.Notification.TemplateCode
            },
            templateParams = parameters
        };

        using var response = await _http.PostAsJsonAsync("campaign/t1/api/v2", payload, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);

        if (response.IsSuccessStatusCode)
        {
            // Correlation rides on the delivery id, recorded as the provider
            // reference so the webhook has something to match against.
            return ChannelSendResult.Accepted(
                messageId: null,
                reference: context.Delivery.Id.ToString(),
                detail: $"Accepted by AiSensy campaign '{campaign}'");
        }

        var outcome = Classify(response.StatusCode, raw);
        var detail  = $"AiSensy {(int)response.StatusCode}: {Truncate(ExtractMessage(raw) ?? raw, 300)}";

        _logger.LogWarning(
            "AiSensy send failed for delivery {DeliveryId} (campaign {Campaign}): {Status} -> {Outcome}",
            context.Delivery.Id, campaign, (int)response.StatusCode, outcome);

        return outcome == ChannelSendOutcome.PermanentFailure
            ? ChannelSendResult.Permanent(detail)
            : ChannelSendResult.Transient(detail);
    }

    /// <summary>
    /// AiSensy reports most problems as a 400 with a message, so the text has to
    /// carry the decision. Campaign and parameter problems are configuration and
    /// will fail identically forever; anything else gets the benefit of the doubt.
    /// </summary>
    internal static ChannelSendOutcome Classify(HttpStatusCode status, string body)
    {
        if (status is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout)
            return ChannelSendOutcome.TransientFailure;

        // Key rotated or revoked: a human fix, but keep the message alive for the
        // retry window rather than discarding it.
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return ChannelSendOutcome.TransientFailure;

        if ((int)status >= 500)
            return ChannelSendOutcome.TransientFailure;

        var text = body.ToLowerInvariant();

        if (text.Contains("campaign") || text.Contains("template") ||
            text.Contains("parameter") || text.Contains("invalid destination") ||
            text.Contains("not approved") || text.Contains("does not exist"))
        {
            return ChannelSendOutcome.PermanentFailure;
        }

        return ChannelSendOutcome.TransientFailure;
    }

    private static string? ExtractMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var name in new[] { "message", "error", "errorMessage" })
            {
                if (doc.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                    return value.GetString();
            }
        }
        catch (JsonException) { }

        return null;
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "...";
}
