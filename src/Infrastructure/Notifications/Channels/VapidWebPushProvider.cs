using System.Net;
using System.Text.Json;
using EventOpsOracle.Application.Notifications.Abstractions;
using EventOpsOracle.Domain.Enums;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AppPushMessage = EventOpsOracle.Application.Notifications.Contracts.PushMessage;

namespace EventOpsOracle.Infrastructure.Notifications.Channels;

/// <summary>
/// Standard W3C Web Push (RFC 8030) with VAPID, spoken straight to whichever push
/// service the browser gave us: FCM for Chromium, Mozilla autopush for Firefox,
/// Apple's for Safari. No Firebase project, no vendor account.
///
/// Payload encryption is aes128gcm (RFC 8291), which is the reason this uses
/// Lib.Net.Http.WebPush rather than the more popular WebPush package: that one
/// still emits the legacy "aesgcm" draft encoding, which Apple rejects outright.
/// Since iOS is the platform this transport exists to reach -- Safari 16.4+ is the
/// only way to notify an iPhone without a native app -- an aesgcm-only library
/// would have failed silently on exactly the devices that matter most.
///
/// This class translates and classifies. It does not decide retries, does not
/// know a user can have several devices, and never touches the database.
/// </summary>
public sealed class VapidWebPushProvider : IPushNotificationProvider
{
    private readonly PushServiceClient _client;
    private readonly WebPushOptions _options;
    private readonly ILogger<VapidWebPushProvider> _logger;

    public VapidWebPushProvider(
        HttpClient httpClient,
        IOptions<WebPushOptions> options,
        ILogger<VapidWebPushProvider> logger)
    {
        _options = options.Value;
        _logger  = logger;

        _client = new PushServiceClient(httpClient)
        {
            DefaultTimeToLive = _options.TimeToLiveSeconds,

            // The library honours a push service's Retry-After for us. Cheap
            // politeness that also keeps 429s from consuming a delivery attempt.
            AutoRetryAfter = true,
            MaxRetriesAfter = 1
        };

        if (_options.HasKeys)
        {
            _client.DefaultAuthentication = new VapidAuthentication(_options.PublicKey, _options.PrivateKey)
            {
                Subject = _options.Subject
            };
        }
    }

    public PushProvider Provider  => PushProvider.WebPush;
    public string ProviderName    => "WebPush";
    public bool IsConfigured      => _options.Enabled && _options.HasKeys;

    public async Task<PushSendResult> SendAsync(
        AppPushMessage message, PushEndpoint endpoint, CancellationToken ct = default)
    {
        if (!IsConfigured)
            return PushSendResult.Transient("Web Push is not configured");

        if (string.IsNullOrWhiteSpace(endpoint.Endpoint)
            || string.IsNullOrWhiteSpace(endpoint.P256dhKey)
            || string.IsNullOrWhiteSpace(endpoint.AuthSecret))
        {
            // A row that cannot be encrypted to is not a transport problem and
            // never will be. Gone, so it gets retired rather than retried.
            return PushSendResult.Gone("Registration is missing endpoint or encryption keys");
        }

        var subscription = new PushSubscription { Endpoint = endpoint.Endpoint };
        subscription.SetKey(PushEncryptionKeyName.P256DH, endpoint.P256dhKey);
        subscription.SetKey(PushEncryptionKeyName.Auth,   endpoint.AuthSecret);

        var push = new PushMessage(Serialize(message))
        {
            // Collapse key: a newer message on the same subject replaces an
            // undelivered older one instead of stacking up on a phone that has
            // been offline. Per notification, so unrelated news is never dropped.
            Topic       = Topic(message),
            Urgency     = MapUrgency(message.Priority),
            TimeToLive  = _options.TimeToLiveSeconds
        };

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            await _client.RequestPushMessageDeliveryAsync(subscription, push, timeout.Token);

            // A push service accepting the message says nothing about the device
            // having shown it, and Web Push has no delivery receipt at all -- so
            // Accepted is the strongest state this channel can ever reach.
            return PushSendResult.Accepted();
        }
        catch (PushServiceClientException ex)
        {
            return Classify(ex.StatusCode, ex.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // worker shutdown -- let the delivery be retried untouched
        }
        catch (OperationCanceledException)
        {
            return PushSendResult.Transient($"Push service timed out after {_options.TimeoutSeconds}s");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Web Push transport failure: {Reason}", ex.Message);
            return PushSendResult.Transient("Network failure reaching the push service");
        }
    }

    /// <summary>
    /// Maps a push service's HTTP response to a verdict. Public so the mapping
    /// can be tested without a push service.
    /// </summary>
    public static PushSendResult Classify(HttpStatusCode status, string? detail = null) => status switch
    {
        // The subscription is gone. Only these two retire a registration.
        HttpStatusCode.NotFound => PushSendResult.Gone("Push service returned 404 Not Found"),
        HttpStatusCode.Gone     => PushSendResult.Gone("Push service returned 410 Gone"),

        // Worth trying again.
        HttpStatusCode.TooManyRequests    => PushSendResult.Transient("Push service rate limited (429)"),
        HttpStatusCode.RequestTimeout     => PushSendResult.Transient("Push service timed out (408)"),
        HttpStatusCode.InternalServerError or
        HttpStatusCode.BadGateway         or
        HttpStatusCode.ServiceUnavailable or
        HttpStatusCode.GatewayTimeout     => PushSendResult.Transient($"Push service unavailable ({(int)status})"),

        // Credential problems are transient on purpose, matching the email
        // sender: a VAPID key mismatch needs a human, and the retry window keeps
        // the notification alive until someone fixes the configuration. It is
        // deliberately NOT treated as a dead subscription -- retiring every
        // registration because a key was wrong would be unrecoverable.
        HttpStatusCode.Unauthorized => PushSendResult.Transient("Push service rejected our VAPID credentials (401)"),
        HttpStatusCode.Forbidden    => PushSendResult.Transient("Push service rejected our VAPID credentials (403)"),

        // Our request is wrong. Sending it again produces the same answer.
        HttpStatusCode.RequestEntityTooLarge => PushSendResult.Permanent("Push payload too large (413)"),
        HttpStatusCode.BadRequest            => PushSendResult.Permanent("Push service rejected the request (400)"),

        _ => (int)status >= 500
            ? PushSendResult.Transient($"Push service error ({(int)status})")
            : PushSendResult.Permanent($"Push service returned {(int)status}")
    };

    /// <summary>
    /// Business priority to Web Push urgency. Urgency is a hint that lets a device
    /// on a metered connection defer low-value messages; it is not a guarantee.
    /// </summary>
    public static PushMessageUrgency MapUrgency(NotificationPriority priority) => priority switch
    {
        NotificationPriority.Critical => PushMessageUrgency.High,
        NotificationPriority.High     => PushMessageUrgency.High,
        NotificationPriority.Normal   => PushMessageUrgency.Normal,
        _                             => PushMessageUrgency.Low
    };

    /// <summary>
    /// Topic must be a short base64url token, so the notification id is trimmed
    /// rather than used whole.
    /// </summary>
    private static string Topic(AppPushMessage message)
        => message.NotificationId.ToString("N")[..16];

    /// <summary>
    /// The wire payload the service worker reads. Kept deliberately small: push
    /// services cap the encrypted body (4KB is the safe assumption), and anything
    /// the client needs beyond this it can fetch from the API once it is awake.
    /// </summary>
    internal static string Serialize(AppPushMessage message)
    {
        var payload = new Dictionary<string, object?>
        {
            ["title"]            = message.Title,
            ["body"]             = message.Body,
            ["notificationId"]   = message.NotificationId,
            ["notificationType"] = message.NotificationType,
            ["deepLink"]         = message.DeepLink,
            ["badgeCount"]       = message.BadgeCount,
            ["sound"]            = message.Sound,
            ["priority"]         = message.Priority.ToString()
        };

        // Only small, non-sensitive extras travel: ids the client can act on.
        if (message.Data is { Count: > 0 })
        {
            payload["data"] = message.Data
                .Where(kv => kv.Value is { Length: > 0 } and { Length: <= 200 })
                .Take(12)
                .ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        return JsonSerializer.Serialize(payload);
    }
}
