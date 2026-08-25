namespace EventOpsOracle.Domain.Enums;

/// <summary>
/// Explicit delivery lifecycle, deliberately NOT a boolean "IsSent" -- the
/// operational question is almost never "did we try" but "where did it get to,
/// and if it failed, why".
///
/// Not every channel reports every state: email can confirm Delivered via SES
/// events, WhatsApp can reach Read, and in-app has no provider at all so it
/// goes straight to Delivered. Accepted means the provider took the message,
/// which is NOT the same as the recipient receiving it.
/// </summary>
public enum NotificationStatus
{
    /// <summary>Persisted and waiting for a worker to claim it.</summary>
    Pending = 0,

    /// <summary>Claimed by a worker and being handed to a provider.</summary>
    Processing = 1,

    /// <summary>The provider accepted the request. Delivery is not yet confirmed.</summary>
    Accepted = 2,

    /// <summary>The provider confirmed the message reached the recipient.</summary>
    Delivered = 3,

    /// <summary>The recipient opened it (WhatsApp read receipts, in-app open).</summary>
    Read = 4,

    /// <summary>Terminal failure: either a permanent error or retries exhausted.</summary>
    Failed = 5,

    /// <summary>Deliberately abandoned before sending (e.g. the event was cancelled).</summary>
    Cancelled = 6
}
