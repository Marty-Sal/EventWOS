using EventWOS.Domain.Common;
using EventWOS.Domain.Enums;

namespace EventWOS.Domain.Entities;

/// <summary>
/// One browser or installed PWA that has agreed to receive push notifications
/// for a user. "Device" is generous: it is really one browser profile on one
/// machine, because that is the granularity the Push API gives us. A crew member
/// with the PWA on their phone and Chrome on a laptop has two rows, and both
/// must be delivered to -- hence the deliberate plural.
///
/// Why this exists as its own table rather than a column on users:
///
///   * Push subscriptions expire and are replaced by the browser at will. They
///     are cache-like data with their own lifecycle, not user attributes.
///   * Push is the one channel with no single stable destination. Email has one
///     address and WhatsApp one number, so NotificationDelivery can carry the
///     destination inline; a user's push endpoints are a set that changes
///     without anyone logging in. So the Push delivery row addresses the USER,
///     and the sender fans out across the rows here at send time.
///
/// Lifecycle rules encoded below:
///
///   * <see cref="Endpoint"/> is the identity of a Web Push subscription, so
///     re-subscribing the same browser must UPDATE this row, never insert a
///     second one. The unique index enforces that even if a caller forgets.
///   * A push service answering 404 or 410 Gone means the subscription is dead
///     for good. Those get <see cref="Deactivate"/>d rather than retried -- an
///     endpoint that is gone does not come back, and retrying it forever is how
///     a delivery backlog turns into a permanent one.
///   * Deactivation is a state, not a delete. The row stays for audit ("we did
///     try to reach this person, their browser had thrown the subscription
///     away") and so a later re-subscribe reactivates a row instead of racing
///     the unique index.
/// </summary>
public sealed class DeviceRegistration : BaseEntity
{
    /// <summary>Longest endpoint URL we will store. Real ones sit near 200 chars.</summary>
    public const int MaxEndpointLength = 500;

    private DeviceRegistration() { }

    /// <summary>
    /// Registers a standard Web Push subscription. The two keys come from the
    /// browser's PushSubscription and are required to encrypt payloads to it --
    /// without them a push can still wake the service worker but carries no
    /// data, so they are not optional.
    /// </summary>
    public static DeviceRegistration ForWebPush(
        Guid     userId,
        string   endpoint,
        string   p256dhKey,
        string   authSecret,
        DateTime nowUtc,
        string?  deviceId  = null,
        string?  platform  = null,
        string?  userAgent = null)
    {
        if (userId == Guid.Empty)                    throw new ArgumentException("A device registration needs an owner.", nameof(userId));
        if (string.IsNullOrWhiteSpace(endpoint))     throw new ArgumentException("Web Push requires an endpoint.", nameof(endpoint));
        if (string.IsNullOrWhiteSpace(p256dhKey))    throw new ArgumentException("Web Push requires the p256dh key.", nameof(p256dhKey));
        if (string.IsNullOrWhiteSpace(authSecret))   throw new ArgumentException("Web Push requires the auth secret.", nameof(authSecret));

        return new DeviceRegistration
        {
            UserId     = userId,
            Provider   = PushProvider.WebPush,
            Endpoint   = endpoint.Trim(),
            P256dhKey  = p256dhKey.Trim(),
            AuthSecret = authSecret.Trim(),
            DeviceId   = Trimmed(deviceId),
            Platform   = Trimmed(platform),
            UserAgent  = Trimmed(userAgent),
            IsActive   = true,
            LastSeenAt = nowUtc,
            CreatedAt  = nowUtc
        };
    }

    /// <summary>
    /// Registers an FCM Web token. Present so a Firebase project can be added
    /// later without a schema change; nothing sends through it yet.
    /// </summary>
    public static DeviceRegistration ForFcm(
        Guid     userId,
        string   pushToken,
        DateTime nowUtc,
        string?  deviceId  = null,
        string?  platform  = null,
        string?  userAgent = null)
    {
        if (userId == Guid.Empty)                 throw new ArgumentException("A device registration needs an owner.", nameof(userId));
        if (string.IsNullOrWhiteSpace(pushToken)) throw new ArgumentException("FCM requires a registration token.", nameof(pushToken));

        return new DeviceRegistration
        {
            UserId     = userId,
            Provider   = PushProvider.Fcm,
            PushToken  = pushToken.Trim(),
            DeviceId   = Trimmed(deviceId),
            Platform   = Trimmed(platform),
            UserAgent  = Trimmed(userAgent),
            IsActive   = true,
            LastSeenAt = nowUtc,
            CreatedAt  = nowUtc
        };
    }

    /// <summary>Owner. Mapped as a real EF relationship -- see the configuration.</summary>
    public Guid UserId { get; private set; }

    /// <summary>Transport this row speaks.</summary>
    public PushProvider Provider { get; private set; }

    /// <summary>
    /// Web Push subscription URL, and the natural key of the subscription. Null
    /// for FCM rows.
    /// </summary>
    public string? Endpoint { get; private set; }

    /// <summary>Subscriber public key (base64url) used to encrypt the payload. Web Push only.</summary>
    public string? P256dhKey { get; private set; }

    /// <summary>Subscriber auth secret (base64url) used to encrypt the payload. Web Push only.</summary>
    public string? AuthSecret { get; private set; }

    /// <summary>FCM registration token, and the natural key of an FCM row. Null for Web Push.</summary>
    public string? PushToken { get; private set; }

    /// <summary>
    /// Client-supplied stable id for this browser profile, so the UI can say
    /// "notifications are on for this device" and offer to turn them off again.
    /// Advisory only: never used for authorization, and never a hardware id --
    /// a web app cannot read a MAC address or IMEI and must not pretend to.
    /// </summary>
    public string? DeviceId { get; private set; }

    /// <summary>Coarse platform label for the admin view, e.g. "Android", "iOS", "Windows".</summary>
    public string? Platform { get; private set; }

    /// <summary>User agent as reported at registration. Display and diagnostics only.</summary>
    public string? UserAgent { get; private set; }

    /// <summary>False once the push service has told us this subscription is gone.</summary>
    public bool IsActive { get; private set; }

    /// <summary>Last time the client confirmed this subscription is still live.</summary>
    public DateTime? LastSeenAt { get; private set; }

    /// <summary>Last time a push was accepted by the push service for this row.</summary>
    public DateTime? LastSuccessAt { get; private set; }

    /// <summary>When it was deactivated, if it was.</summary>
    public DateTime? DeactivatedAt { get; private set; }

    /// <summary>Why it was deactivated -- "410 Gone", "user disabled", and so on.</summary>
    public string? DeactivationReason { get; private set; }

    /// <summary>
    /// Consecutive transient failures. Reset by a success. Lets an endpoint that
    /// is merely broken rather than gone be retired eventually, without treating
    /// one flaky night as permanent.
    /// </summary>
    public int ConsecutiveFailures { get; private set; }

    /// <summary>
    /// The same browser re-subscribing, or simply checking in. Reactivates a row
    /// that had been retired, because a browser that just handed us a live
    /// subscription is by definition reachable again.
    /// </summary>
    public void Touch(DateTime nowUtc, string? platform = null, string? userAgent = null, string? deviceId = null)
    {
        IsActive            = true;
        DeactivatedAt       = null;
        DeactivationReason  = null;
        ConsecutiveFailures = 0;
        LastSeenAt          = nowUtc;
        UpdatedAt           = nowUtc;

        // Only overwrite descriptive fields when the caller actually supplied
        // something, so a bare heartbeat cannot blank out what we knew.
        if (Trimmed(platform)  is { } p) Platform  = p;
        if (Trimmed(userAgent) is { } u) UserAgent = u;
        if (Trimmed(deviceId)  is { } d) DeviceId  = d;
    }

    /// <summary>
    /// The browser kept the endpoint but rotated its encryption keys. Rare, but
    /// legal, and sending with stale keys fails permanently -- so accept them.
    /// </summary>
    public void RotateKeys(string p256dhKey, string authSecret, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(p256dhKey))  throw new ArgumentException("p256dh key is required.", nameof(p256dhKey));
        if (string.IsNullOrWhiteSpace(authSecret)) throw new ArgumentException("Auth secret is required.", nameof(authSecret));

        P256dhKey  = p256dhKey.Trim();
        AuthSecret = authSecret.Trim();
        UpdatedAt  = nowUtc;
    }

    /// <summary>A push service accepted a message for this subscription.</summary>
    public void RecordSuccess(DateTime nowUtc)
    {
        ConsecutiveFailures = 0;
        LastSuccessAt       = nowUtc;
        UpdatedAt           = nowUtc;
    }

    /// <summary>
    /// A transient failure (timeout, 429, 5xx). Counted but not fatal: the
    /// delivery's own retry schedule decides when to try again.
    /// </summary>
    public void RecordTransientFailure(DateTime nowUtc)
    {
        ConsecutiveFailures++;
        UpdatedAt = nowUtc;
    }

    /// <summary>
    /// Retire this subscription for good -- 404/410 from the push service, or the
    /// user turning notifications off. Idempotent: repeated calls keep the first
    /// reason, since that is the one that explains what happened.
    /// </summary>
    public void Deactivate(string reason, DateTime nowUtc)
    {
        if (!IsActive) return;

        IsActive           = false;
        DeactivatedAt      = nowUtc;
        DeactivationReason = string.IsNullOrWhiteSpace(reason)
            ? "Deactivated"
            : reason.Trim()[..Math.Min(reason.Trim().Length, 200)];
        UpdatedAt = nowUtc;
    }

    private static string? Trimmed(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
