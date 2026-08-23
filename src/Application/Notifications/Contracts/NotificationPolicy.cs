using EventWOS.Domain.Enums;

namespace EventWOS.Application.Notifications.Contracts;

/// <summary>
/// Default priority and channel set per notification type -- the business
/// policy of "how urgent is this, and is it worth a WhatsApp message".
///
/// This is a default, not a decision. The final channel set is the intersection
/// of these, the templates actually active in the database, and the contact
/// details the recipient has. That layering is what lets an admin switch off a
/// channel for one notification type (deactivate its template) without a
/// deployment, and it is where per-user preferences will slot in later without
/// touching any of this.
/// </summary>
public static class NotificationPolicy
{
    private static readonly NotificationChannel[] InAppOnly       = { NotificationChannel.InApp };
    private static readonly NotificationChannel[] InAppEmail      = { NotificationChannel.InApp, NotificationChannel.Email };
    private static readonly NotificationChannel[] InAppWhatsApp   = { NotificationChannel.InApp, NotificationChannel.WhatsApp };
    private static readonly NotificationChannel[] AllChannels     = { NotificationChannel.InApp, NotificationChannel.Email, NotificationChannel.WhatsApp };

    private static readonly Dictionary<string, (NotificationPriority Priority, NotificationChannel[] Channels)> Defaults =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Account lifecycle: reaches people who are not logged in yet, so
            // in-app alone would be useless.
            [NotificationTemplateCodes.AccountApproved]        = (NotificationPriority.High,     AllChannels),
            [NotificationTemplateCodes.AccountRejected]        = (NotificationPriority.High,     InAppEmail),
            [NotificationTemplateCodes.AccountInvited]         = (NotificationPriority.High,     AllChannels),
            [NotificationTemplateCodes.ProfileCompleted]       = (NotificationPriority.Low,      InAppEmail),
            // OTP is security-critical and time-boxed. Never in-app: the whole
            // point is to reach a channel the person already controls.
            [NotificationTemplateCodes.PasswordResetOtp]       = (NotificationPriority.Critical, new[] { NotificationChannel.Email, NotificationChannel.WhatsApp }),

            [NotificationTemplateCodes.VendorEventInvited]     = (NotificationPriority.Normal,   AllChannels),
            [NotificationTemplateCodes.VendorInviteRevoked]    = (NotificationPriority.Normal,   InAppEmail),
            [NotificationTemplateCodes.VendorEventReminder]    = (NotificationPriority.Normal,   InAppWhatsApp),

            // Crew are field staff: WhatsApp is the channel they actually read,
            // and many have no working email address on file.
            [NotificationTemplateCodes.CrewInvitation]         = (NotificationPriority.Normal,   InAppWhatsApp),
            [NotificationTemplateCodes.CrewAssignment]         = (NotificationPriority.Normal,   InAppWhatsApp),
            [NotificationTemplateCodes.CrewAssignmentApproved] = (NotificationPriority.Normal,   InAppWhatsApp),
            [NotificationTemplateCodes.CrewAssignmentRejected] = (NotificationPriority.Normal,   InAppWhatsApp),
            [NotificationTemplateCodes.CrewInviteRevoked]      = (NotificationPriority.Normal,   InAppWhatsApp),
            [NotificationTemplateCodes.CrewAssignmentReminder] = (NotificationPriority.High,     InAppWhatsApp),

            [NotificationTemplateCodes.EventAnnouncement]      = (NotificationPriority.Normal,   AllChannels),
            [NotificationTemplateCodes.EventUpdated]           = (NotificationPriority.High,     InAppWhatsApp),
            // Someone travelling to a cancelled event is the worst failure this
            // system can have, so it outranks ordinary traffic.
            [NotificationTemplateCodes.EventCancelled]         = (NotificationPriority.Critical, AllChannels),
            [NotificationTemplateCodes.EventStarting]          = (NotificationPriority.High,     InAppWhatsApp),
            [NotificationTemplateCodes.ShiftChanged]           = (NotificationPriority.High,     InAppWhatsApp),

            [NotificationTemplateCodes.AttendanceReminder]     = (NotificationPriority.High,     InAppWhatsApp),
            [NotificationTemplateCodes.CheckInVerified]        = (NotificationPriority.Low,      InAppOnly),

            // Money: people chase these, and a WhatsApp line saves a phone call.
            [NotificationTemplateCodes.PaymentApproved]        = (NotificationPriority.Normal,   InAppWhatsApp),
            [NotificationTemplateCodes.PaymentRejected]        = (NotificationPriority.Normal,   InAppWhatsApp),
            [NotificationTemplateCodes.PayrollReleased]        = (NotificationPriority.Normal,   AllChannels),
        };

    /// <summary>
    /// Unknown codes fall back to Normal/in-app rather than throwing: a new
    /// template code shipped ahead of its policy entry should still notify
    /// someone, quietly and cheaply, instead of failing a business operation.
    /// </summary>
    public static NotificationPriority DefaultPriority(string templateCode)
        => Defaults.TryGetValue(templateCode, out var entry) ? entry.Priority : NotificationPriority.Normal;

    public static IReadOnlyCollection<NotificationChannel> DefaultChannels(string templateCode)
        => Defaults.TryGetValue(templateCode, out var entry) ? entry.Channels : InAppOnly;

    public static bool IsKnown(string templateCode) => Defaults.ContainsKey(templateCode);

    public static IReadOnlyCollection<string> AllCodes => Defaults.Keys.ToArray();
}
