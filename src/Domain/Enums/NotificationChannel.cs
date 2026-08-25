namespace EventOpsOracle.Domain.Enums;

/// <summary>
/// Delivery channels a notification can fan out to. Each channel gets its own
/// <see cref="EventOpsOracle.Domain.Entities.NotificationDelivery"/> row with
/// independent state, because a failed email must not affect a delivered
/// WhatsApp message.
/// </summary>
public enum NotificationChannel
{
    /// <summary>
    /// In-app: persisted for the recipient's notification list and pushed live
    /// over SignalR when they happen to be connected. This is the only channel
    /// that is delivered inside our own system, so it has no external provider.
    /// </summary>
    InApp = 0,

    Email = 1,

    WhatsApp = 2,

    /// <summary>Architecture-ready. No provider implementation yet.</summary>
    Sms = 3,

    /// <summary>Mobile push (FCM). Architecture-ready.</summary>
    Push = 4
}
