namespace EventWOS.Infrastructure.Notifications.Channels;

/// <summary>
/// VAPID configuration for Web Push. Bound from the "WebPush" section, which on
/// Railway means WebPush__PublicKey / WebPush__PrivateKey / WebPush__Subject.
///
/// The public key is deliberately NOT a secret -- the browser needs it to
/// subscribe, and it is served to the client. The private key is, and it never
/// leaves the server: it signs the VAPID JWT that proves to a push service that
/// this really is EventWOS.
/// </summary>
public sealed class WebPushOptions
{
    public const string SectionName = "WebPush";

    /// <summary>
    /// VAPID application server public key (base64url, P-256). Handed to the
    /// browser as applicationServerKey when it subscribes.
    /// </summary>
    public string PublicKey { get; set; } = string.Empty;

    /// <summary>VAPID private key (base64url). Server only. Never logged, never returned by an API.</summary>
    public string PrivateKey { get; set; } = string.Empty;

    /// <summary>
    /// Contact for the push service operator if our traffic misbehaves. Must be a
    /// mailto: or https: URI -- push services reject anything else, and some
    /// reject an empty subject outright.
    /// </summary>
    public string Subject { get; set; } = "mailto:support@eventwos.in";

    /// <summary>
    /// How long a push service should hold a message for a device that is
    /// offline, in seconds. Six hours by default: an event notification that
    /// arrives a day late is worse than useless, but a phone in a bag for the
    /// afternoon should still get it.
    /// </summary>
    public int TimeToLiveSeconds { get; set; } = 21_600;

    /// <summary>Per-request timeout. Push services are usually fast; a slow one should not hold a worker slot.</summary>
    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// Master switch, so a staging deployment can hold the keys without being
    /// able to push to anyone. Defaults true; the provider still reports itself
    /// unconfigured unless both keys are present.
    /// </summary>
    public bool Enabled { get; set; } = true;

    public bool HasKeys =>
        !string.IsNullOrWhiteSpace(PublicKey) && !string.IsNullOrWhiteSpace(PrivateKey);
}
