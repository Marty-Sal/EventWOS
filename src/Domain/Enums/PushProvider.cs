namespace EventWOS.Domain.Enums;

/// <summary>
/// Which push transport a <see cref="Entities.DeviceRegistration"/> speaks.
///
/// Two, deliberately, for the same reason the WhatsApp channel has both Meta and
/// AiSensy: the transport is a deployment choice, not an architectural one.
/// </summary>
public enum PushProvider
{
    /// <summary>
    /// Standard W3C Web Push (RFC 8030) with VAPID, spoken directly to whatever
    /// push service the browser hands us -- FCM for Chromium, Mozilla autopush
    /// for Firefox, Apple's for Safari. No third-party account, and it is the
    /// only transport that works on iOS, where Safari 16.4+ supports Web Push
    /// but only for a PWA the user has added to the home screen.
    /// </summary>
    WebPush = 1,

    /// <summary>
    /// Firebase Cloud Messaging Web, addressed by FCM registration token rather
    /// than a subscription endpoint. Kept as a distinct provider so a Firebase
    /// project can be introduced later without disturbing existing rows.
    /// </summary>
    Fcm = 2
}
