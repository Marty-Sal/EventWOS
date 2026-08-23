using EventWOS.Domain.Enums;

namespace EventWOS.Application.Notifications.Contracts;

/// <summary>
/// What EventWOS wants shown on a device, in EventWOS's own terms. Deliberately
/// free of transport detail: no VAPID, no FCM, no JSON shape. Providers translate
/// this into whatever their protocol wants, which is what keeps the Application
/// layer from growing a dependency on a push vendor.
/// </summary>
/// <param name="Title">Notification headline, e.g. "New Shift Assigned".</param>
/// <param name="Body">One or two lines of detail.</param>
/// <param name="NotificationId">Our notification id, so the client can mark it read.</param>
/// <param name="NotificationType">Template code, e.g. CREW_ASSIGNMENT. Used for grouping/tagging.</param>
/// <param name="DeepLink">Site-relative path to open on click. Always relative -- see <see cref="PushDeepLinks"/>.</param>
/// <param name="BadgeCount">
/// The recipient's authoritative unread count at send time, straight from the
/// database. Never "how many pushes we have sent": the server owns this number,
/// so a user who reads one on their phone sees the laptop badge fall too.
/// </param>
/// <param name="Priority">Business urgency, mapped by each provider to whatever it supports.</param>
/// <param name="Sound">Sound hint. "default" today; the field exists so a custom tone can arrive later without a payload change.</param>
/// <param name="Data">Small extra values for the client (event id, payment id). Never secrets.</param>
public sealed record PushMessage(
    string Title,
    string Body,
    Guid NotificationId,
    string NotificationType,
    string DeepLink,
    int BadgeCount,
    NotificationPriority Priority,
    string? Sound = "default",
    IReadOnlyDictionary<string, string?>? Data = null);
