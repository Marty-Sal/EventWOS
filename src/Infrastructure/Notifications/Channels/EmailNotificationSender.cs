using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using System.Web;
using EventOpsOracle.Application.Notifications.Abstractions;
using EventOpsOracle.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EventOpsOracle.Infrastructure.Notifications.Channels;

/// <summary>
/// SendGrid sender for the notification pipeline.
///
/// Deliberately separate from the existing SendGridEmailService rather than
/// reusing it. That service answers a bool, which is all a user-facing flow
/// needs, but a delivery row needs three things it cannot provide: the
/// X-Message-Id for webhook correlation, custom_args so an event coming back can
/// be tied to a delivery, and the transient/permanent distinction that decides
/// whether to retry. A bool would have meant every bounce looked identical to a
/// rate limit.
///
/// The older service stays exactly as it is -- registration, approval and OTP
/// mail keep working untouched.
/// </summary>
public sealed class EmailNotificationSender : INotificationChannelSender
{
    private readonly HttpClient _http;
    private readonly EmailSenderOptions _options;
    private readonly ILogger<EmailNotificationSender> _logger;

    public EmailNotificationSender(
        HttpClient http, IOptions<EmailSenderOptions> options, ILogger<EmailNotificationSender> logger)
    {
        _http    = http;
        _options = options.Value;
        _logger  = logger;
    }

    public NotificationChannel Channel => NotificationChannel.Email;

    public string ProviderName => "SendGrid";

    public bool IsConfigured => _options.IsConfigured;

    public async Task<ChannelSendResult> SendAsync(NotificationSendContext context, CancellationToken ct = default)
    {
        var to = context.Destination?.Trim();
        if (string.IsNullOrWhiteSpace(to) || !to.Contains('@'))
        {
            // A data problem, not a send problem: nothing to retry and nothing to
            // fix at this end.
            return ChannelSendResult.Skip($"Unusable email address '{context.Destination}'");
        }

        var subject = string.IsNullOrWhiteSpace(context.Message.Subject)
            // Every mail needs a subject line, and a blank one both looks broken
            // and hurts deliverability. The template code is a poor headline, so
            // fall back to something a human would recognise.
            ? "OpsOracle notification"
            : context.Message.Subject!;

        var html = context.Message.Body;

        var payload = new
        {
            personalizations = new[]
            {
                new
                {
                    to = new[] { new { email = to } },

                    // Echoed back on every event webhook. This is the correlation
                    // handle: sg_message_id gains suffixes in flight, so matching
                    // on our own delivery id is far more reliable.
                    custom_args = new Dictionary<string, string>
                    {
                        ["deliveryId"]     = context.Delivery.Id.ToString(),
                        ["notificationId"] = context.Notification.Id.ToString(),
                        ["code"]           = context.Notification.TemplateCode
                    }
                }
            },
            from    = new { email = _options.FromEmail, name = _options.FromName },
            subject,
            content = new[]
            {
                // Both parts, plain text first as the spec requires. A
                // multipart mail is filtered as spam far less often than
                // HTML alone.
                new { type = "text/plain", value = ToPlainText(html) },
                new { type = "text/html",  value = html }
            },

            // Lets the notification type be filtered in SendGrid's own UI, which
            // is where someone will look when asked "did the crew get it?".
            categories   = new[] { "eventwos", context.Notification.TemplateCode.ToLowerInvariant() },
            mail_settings = new { sandbox_mode = new { enable = _options.SandboxMode } }
        };

        using var response = await _http.PostAsJsonAsync("v3/mail/send", payload, ct);

        if (response.IsSuccessStatusCode)
        {
            // SendGrid returns 202 with an empty body; the id lives in a header.
            var messageId = response.Headers.TryGetValues("X-Message-Id", out var values)
                ? values.FirstOrDefault()
                : null;

            return ChannelSendResult.Accepted(
                messageId,
                reference: context.Delivery.Id.ToString(),
                detail: _options.SandboxMode ? "Accepted by SendGrid (sandbox mode -- not sent)" : "Accepted by SendGrid");
        }

        var raw     = await response.Content.ReadAsStringAsync(ct);
        var outcome = Classify(response.StatusCode);
        var detail  = $"SendGrid {(int)response.StatusCode}: {Truncate(raw, 300)}";

        _logger.LogWarning(
            "SendGrid send failed for delivery {DeliveryId}: {Status} -> {Outcome}",
            context.Delivery.Id, (int)response.StatusCode, outcome);

        return outcome == ChannelSendOutcome.PermanentFailure
            ? ChannelSendResult.Permanent(detail)
            : ChannelSendResult.Transient(detail);
    }

    internal static ChannelSendOutcome Classify(HttpStatusCode status) => status switch
    {
        HttpStatusCode.TooManyRequests => ChannelSendOutcome.TransientFailure,
        HttpStatusCode.RequestTimeout  => ChannelSendOutcome.TransientFailure,

        // Key rotated or revoked, or the sender identity is not verified yet.
        // Transient so the message survives the human fix.
        HttpStatusCode.Unauthorized    => ChannelSendOutcome.TransientFailure,
        HttpStatusCode.Forbidden       => ChannelSendOutcome.TransientFailure,

        >= HttpStatusCode.InternalServerError => ChannelSendOutcome.TransientFailure,

        // 400 and 413 mean the request itself is wrong -- a malformed address or
        // an oversized payload will be equally wrong on every retry.
        _ => ChannelSendOutcome.PermanentFailure
    };

    /// <summary>
    /// Plain-text alternative derived from the HTML body. Crude by design: our
    /// templates are simple paragraphs, and a full HTML-to-text library would be
    /// a dependency earning its keep on nothing.
    /// </summary>
    internal static string ToPlainText(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;

        // Block boundaries become line breaks before tags are stripped, otherwise
        // every paragraph runs into the next one.
        var text = Regex.Replace(html, @"<\s*br\s*/?\s*>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</\s*(p|div|h[1-6]|li|tr)\s*>", "\n\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<[^>]+>", string.Empty);
        text = HttpUtility.HtmlDecode(text);

        // Collapse the runs of blank lines the substitutions leave behind.
        text = Regex.Replace(text, @"[ \t]+", " ");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");

        return text.Trim();
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "...";
}
