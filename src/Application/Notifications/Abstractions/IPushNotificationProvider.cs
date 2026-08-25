using EventOpsOracle.Application.Notifications.Contracts;
using EventOpsOracle.Domain.Enums;

namespace EventOpsOracle.Application.Notifications.Abstractions;

/// <summary>
/// One push transport, addressing one endpoint at a time.
///
/// Kept separate from <see cref="INotificationChannelSender"/> on purpose: the
/// channel sender owns the fan-out across a user's devices and the bookkeeping
/// that follows, while a provider does nothing but speak the protocol. That split
/// is what lets Web Push and FCM Web coexist without either of them knowing that
/// a user can have several devices.
/// </summary>
public interface IPushNotificationProvider
{
    /// <summary>Which registrations this provider can address.</summary>
    PushProvider Provider { get; }

    /// <summary>Name recorded on the delivery row, e.g. "WebPush".</summary>
    string ProviderName { get; }

    /// <summary>Credentials present and valid. False means "do not queue push at all".</summary>
    bool IsConfigured { get; }

    Task<PushSendResult> SendAsync(PushMessage message, PushEndpoint endpoint, CancellationToken ct = default);
}

/// <summary>
/// One device's addressing details, flattened out of DeviceRegistration so a
/// provider never sees a domain entity it might be tempted to mutate.
/// </summary>
public sealed record PushEndpoint(
    Guid RegistrationId,
    PushProvider Provider,
    string? Endpoint,
    string? P256dhKey,
    string? AuthSecret,
    string? PushToken);

/// <summary>
/// Per-endpoint outcome. Same three-way shape as the channel senders, for the
/// same reason: the difference between "try again" and "stop" is the whole
/// retry policy, and a bool cannot express it.
/// </summary>
public enum PushSendOutcome
{
    /// <summary>The push service took it. Says nothing about the device having shown it.</summary>
    Accepted = 0,

    /// <summary>Timeout, 429, 5xx. Worth another attempt later.</summary>
    TransientFailure = 1,

    /// <summary>
    /// Malformed request, bad credentials, payload too large. Retrying sends the
    /// same broken request again, so it stops here -- but the subscription itself
    /// may well be fine, so it is NOT deactivated.
    /// </summary>
    PermanentFailure = 2,

    /// <summary>
    /// 404/410: the push service says this subscription no longer exists. The
    /// only outcome that retires the registration, because it is the only one
    /// that means the endpoint itself is dead.
    /// </summary>
    EndpointGone = 3
}

/// <param name="ProviderMessageId">Push services rarely give one; recorded when they do.</param>
/// <param name="Detail">Short reason. Never a token, never a credential.</param>
public sealed record PushSendResult(
    PushSendOutcome Outcome,
    string? ProviderMessageId = null,
    string? Detail = null)
{
    public static PushSendResult Accepted(string? messageId = null) => new(PushSendOutcome.Accepted, messageId);
    public static PushSendResult Transient(string detail)           => new(PushSendOutcome.TransientFailure, Detail: detail);
    public static PushSendResult Permanent(string detail)           => new(PushSendOutcome.PermanentFailure, Detail: detail);
    public static PushSendResult Gone(string detail)                => new(PushSendOutcome.EndpointGone, Detail: detail);
}
