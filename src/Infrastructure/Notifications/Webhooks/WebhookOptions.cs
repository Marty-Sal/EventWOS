namespace EventOpsOracle.Infrastructure.Notifications.Webhooks;

/// <summary>
/// Secrets for verifying inbound provider callbacks, bound from "Webhooks".
/// </summary>
public sealed class WebhookOptions
{
    public const string SectionName = "Webhooks";

    /// <summary>SendGrid's ECDSA verification key (base64), from Settings -> Mail Settings -> Signed Event Webhook.</summary>
    public string? SendGridPublicKey { get; set; }

    /// <summary>Meta app secret, used for the X-Hub-Signature-256 HMAC.</summary>
    public string? MetaAppSecret { get; set; }

    /// <summary>Token echoed back during Meta's webhook subscription handshake.</summary>
    public string? MetaVerifyToken { get; set; }

    /// <summary>
    /// Shared secret for AiSensy, which does not sign its callbacks. Sent as a
    /// header or query token -- weaker than a signature, but it is what they offer.
    /// </summary>
    public string? AiSensySecret { get; set; }

    /// <summary>
    /// Accept unverifiable callbacks. Local development only: these endpoints are
    /// anonymous and mutate delivery state, so in production an unsigned request
    /// is indistinguishable from a forged one.
    /// </summary>
    public bool AllowUnsigned { get; set; }
}
