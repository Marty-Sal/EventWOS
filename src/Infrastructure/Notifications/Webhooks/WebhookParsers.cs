using System.Text.Json;
using EventOpsOracle.Application.Notifications.Commands;
using Microsoft.Extensions.Logging;

namespace EventOpsOracle.Infrastructure.Notifications.Webhooks;

/// <summary>
/// SendGrid event webhook payloads: a JSON array of event objects.
///
/// Correlation uses the custom_args.deliveryId we attached at send time, not
/// sg_message_id -- SendGrid appends suffixes to that in flight, so matching on
/// their id means matching on a moving target.
/// </summary>
public static class SendGridWebhookParser
{
    public static IReadOnlyList<ProviderDeliveryEvent> Parse(string rawBody, ILogger logger)
    {
        var events = new List<ProviderDeliveryEvent>();
        if (string.IsNullOrWhiteSpace(rawBody)) return events;

        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return events;

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                // TryGetProperty throws on a non-object element, so the shape has
                // to be checked before it is questioned.
                if (item.ValueKind != JsonValueKind.Object) continue;

                var eventName = item.TryGetProperty("event", out var e) ? e.GetString() : null;
                if (eventName is null) continue;

                var type = eventName.ToLowerInvariant() switch
                {
                    "delivered"           => ProviderDeliveryEventType.Delivered,
                    "open"                => ProviderDeliveryEventType.Read,

                    // Bounce, block, invalid address, or the recipient calling it
                    // spam. All final: retrying sends to the same dead address.
                    "bounce" or "dropped" or "blocked" or "spamreport"
                                          => ProviderDeliveryEventType.Failed,

                    // SendGrid is still trying (full mailbox, greylisting).
                    "deferred"            => ProviderDeliveryEventType.Deferred,

                    // click / unsubscribe / group changes say nothing about delivery.
                    _                     => ProviderDeliveryEventType.Ignored
                };

                if (type == ProviderDeliveryEventType.Ignored) continue;

                var deliveryId = item.TryGetProperty("deliveryId", out var d) && Guid.TryParse(d.GetString(), out var parsed)
                    ? parsed
                    : (Guid?)null;

                // sg_message_id looks like "<X-Message-Id>.filterXXXX-...". We
                // stored the header value, so cut at the first dot to match.
                var messageId = item.TryGetProperty("sg_message_id", out var m) ? m.GetString() : null;
                if (messageId is not null)
                {
                    var dot = messageId.IndexOf('.');
                    if (dot > 0) messageId = messageId[..dot];
                }

                var occurredAt = item.TryGetProperty("timestamp", out var ts) && ts.TryGetInt64(out var unix)
                    ? DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime
                    : DateTime.UtcNow;

                var detail = item.TryGetProperty("reason", out var r) ? r.GetString()
                           : item.TryGetProperty("response", out var resp) ? resp.GetString()
                           : null;

                events.Add(new ProviderDeliveryEvent(deliveryId, messageId, type, Trim(detail, eventName), occurredAt));
            }
        }
        catch (JsonException ex)
        {
            // Never 500 on a malformed payload: providers disable endpoints that
            // keep erroring, and one bad batch must not cost the whole feed.
            logger.LogWarning(ex, "Could not parse SendGrid webhook payload");
        }

        return events;
    }

    private static string Trim(string? detail, string eventName)
        => string.IsNullOrWhiteSpace(detail) ? eventName : (detail.Length > 300 ? detail[..300] : detail);
}

/// <summary>
/// Meta WhatsApp status callbacks, nested as
/// entry[] -> changes[] -> value.statuses[]. Correlated by the wamid returned at
/// send time.
/// </summary>
public static class MetaWhatsAppWebhookParser
{
    public static IReadOnlyList<ProviderDeliveryEvent> Parse(string rawBody, ILogger logger)
    {
        var events = new List<ProviderDeliveryEvent>();
        if (string.IsNullOrWhiteSpace(rawBody)) return events;

        try
        {
            using var doc = JsonDocument.Parse(rawBody);

            // An unexpected top-level array would make TryGetProperty throw.
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return events;

            if (!doc.RootElement.TryGetProperty("entry", out var entries) ||
                entries.ValueKind != JsonValueKind.Array)
            {
                return events;
            }

            foreach (var entry in entries.EnumerateArray())
            {
                if (!entry.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var change in changes.EnumerateArray())
                {
                    if (change.ValueKind != JsonValueKind.Object) continue;

                    if (!change.TryGetProperty("value", out var value) ||
                        !value.TryGetProperty("statuses", out var statuses) ||
                        statuses.ValueKind != JsonValueKind.Array)
                    {
                        // Inbound messages from users arrive on the same webhook.
                        // Not our concern here.
                        continue;
                    }

                    foreach (var status in statuses.EnumerateArray())
                    {
                        if (status.ValueKind != JsonValueKind.Object) continue;

                        var name = status.TryGetProperty("status", out var s) ? s.GetString() : null;
                        if (name is null) continue;

                        var type = name.ToLowerInvariant() switch
                        {
                            "delivered" => ProviderDeliveryEventType.Delivered,
                            "read"      => ProviderDeliveryEventType.Read,
                            "failed"    => ProviderDeliveryEventType.Failed,

                            // "sent" only repeats what the send call already told us.
                            _           => ProviderDeliveryEventType.Ignored
                        };

                        if (type == ProviderDeliveryEventType.Ignored) continue;

                        var messageId = status.TryGetProperty("id", out var id) ? id.GetString() : null;

                        var occurredAt = status.TryGetProperty("timestamp", out var ts) &&
                                         long.TryParse(ts.GetString() ?? ts.ToString(), out var unix)
                            ? DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime
                            : DateTime.UtcNow;

                        events.Add(new ProviderDeliveryEvent(null, messageId, type, ExtractError(status), occurredAt));
                    }
                }
            }
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Could not parse Meta WhatsApp webhook payload");
        }

        return events;
    }

    /// <summary>Meta's per-status errors[] carries the human-readable cause of a failure.</summary>
    private static string? ExtractError(JsonElement status)
    {
        if (!status.TryGetProperty("errors", out var errors) ||
            errors.ValueKind != JsonValueKind.Array ||
            errors.GetArrayLength() == 0)
        {
            return null;
        }

        var first = errors[0];
        var code  = first.TryGetProperty("code", out var c) ? c.ToString() : "?";
        var title = first.TryGetProperty("title", out var t) ? t.GetString()
                  : first.TryGetProperty("message", out var m) ? m.GetString()
                  : null;

        return $"code={code}: {title ?? "unspecified"}";
    }
}

/// <summary>
/// AiSensy callbacks. Shape varies by account configuration, so the delivery id
/// is looked for in several places -- it is the attribute we injected at send
/// time, and the only dependable correlation handle, since their send API returns
/// no message id at all.
/// </summary>
public static class AiSensyWebhookParser
{
    public static IReadOnlyList<ProviderDeliveryEvent> Parse(string rawBody, ILogger logger)
    {
        var events = new List<ProviderDeliveryEvent>();
        if (string.IsNullOrWhiteSpace(rawBody)) return events;

        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            var root = doc.RootElement;

            // Single object or a batch -- both shapes have been observed.
            var items = root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray().ToList()
                : new List<JsonElement> { root };

            foreach (var item in items)
            {
                if (item.ValueKind != JsonValueKind.Object) continue;

                var name = FirstString(item, "status", "eventType", "event");
                if (name is null) continue;

                var type = name.ToLowerInvariant() switch
                {
                    "delivered" or "delivery" => ProviderDeliveryEventType.Delivered,
                    "read" or "seen"          => ProviderDeliveryEventType.Read,
                    "failed" or "undelivered" => ProviderDeliveryEventType.Failed,
                    "sent"                    => ProviderDeliveryEventType.Ignored,
                    _                         => ProviderDeliveryEventType.Ignored
                };

                if (type == ProviderDeliveryEventType.Ignored) continue;

                events.Add(new ProviderDeliveryEvent(
                    FindDeliveryId(item),
                    FirstString(item, "messageId", "wamid", "id"),
                    type,
                    FirstString(item, "reason", "errorMessage", "message"),
                    ParseTimestamp(item)));
            }
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Could not parse AiSensy webhook payload");
        }

        return events;
    }

    /// <summary>Checks the attributes object, the top level, and the tags list.</summary>
    private static Guid? FindDeliveryId(JsonElement item)
    {
        if (item.TryGetProperty("attributes", out var attributes) &&
            attributes.ValueKind == JsonValueKind.Object &&
            attributes.TryGetProperty("deliveryId", out var fromAttributes) &&
            Guid.TryParse(fromAttributes.GetString(), out var parsed))
        {
            return parsed;
        }

        if (item.TryGetProperty("deliveryId", out var direct) && Guid.TryParse(direct.GetString(), out var parsedDirect))
            return parsedDirect;

        if (item.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
        {
            foreach (var tag in tags.EnumerateArray())
            {
                var value = tag.GetString();
                if (value is not null &&
                    value.StartsWith("delivery:", StringComparison.OrdinalIgnoreCase) &&
                    Guid.TryParse(value["delivery:".Length..], out var parsedTag))
                {
                    return parsedTag;
                }
            }
        }

        return null;
    }

    private static string? FirstString(JsonElement item, params string[] names)
    {
        foreach (var name in names)
        {
            if (item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        }

        return null;
    }

    private static DateTime ParseTimestamp(JsonElement item)
    {
        foreach (var name in new[] { "timestamp", "eventTime", "occurredAt" })
        {
            if (!item.TryGetProperty(name, out var value)) continue;

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var unix))
                return DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;

            if (value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (long.TryParse(text, out var unixText))
                    return DateTimeOffset.FromUnixTimeSeconds(unixText).UtcDateTime;

                if (DateTime.TryParse(text, out var parsed))
                    return parsed.ToUniversalTime();
            }
        }

        return DateTime.UtcNow;
    }
}
