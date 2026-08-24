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
    // Push rides along with in-app on almost everything. It is the cheapest
    // channel we have, it only ever reaches a device whose owner explicitly
    // opted in, and a recipient with no registered device is skipped rather
    // than failed -- so adding it costs nothing where it is not wanted.
    private static readonly NotificationChannel[] InAppOnly       = { NotificationChannel.InApp };
    private static readonly NotificationChannel[] InAppEmail      = { NotificationChannel.InApp, NotificationChannel.Email };
    private static readonly NotificationChannel[] InAppPush       = { NotificationChannel.InApp, NotificationChannel.Push };
    private static readonly NotificationChannel[] InAppEmailPush  = { NotificationChannel.InApp, NotificationChannel.Email, NotificationChannel.Push };
    private static readonly NotificationChannel[] InAppWhatsAppPush = { NotificationChannel.InApp, NotificationChannel.WhatsApp, NotificationChannel.Push };
    private static readonly NotificationChannel[] AllChannelsPush   = { NotificationChannel.InApp, NotificationChannel.Email, NotificationChannel.WhatsApp, NotificationChannel.Push };

    private static readonly Dictionary<string, (NotificationPriority Priority, NotificationChannel[] Channels)> Defaults =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Account lifecycle: reaches people who are not logged in yet, so
            // in-app alone would be useless.
            [NotificationTemplateCodes.AccountApproved]        = (NotificationPriority.High,     AllChannelsPush),
            [NotificationTemplateCodes.AccountRejected]        = (NotificationPriority.High,     InAppEmailPush),
            [NotificationTemplateCodes.AccountInvited]         = (NotificationPriority.High,     AllChannelsPush),
            // Low-value housekeeping for whoever manages the person. Deliberately
            // NOT pushed: interrupting a phone for it is how people learn to
            // swipe our notifications away without reading them.
            [NotificationTemplateCodes.ProfileCompleted]       = (NotificationPriority.Low,      InAppEmail),
            // OTP is security-critical and time-boxed. Never in-app: the whole
            // point is to reach a channel the person already controls.
            [NotificationTemplateCodes.PasswordResetOtp]       = (NotificationPriority.Critical, new[] { NotificationChannel.Email, NotificationChannel.WhatsApp }),

            [NotificationTemplateCodes.VendorEventInvited]     = (NotificationPriority.Normal,   AllChannelsPush),
            [NotificationTemplateCodes.VendorInviteRevoked]    = (NotificationPriority.Normal,   InAppEmailPush),
            [NotificationTemplateCodes.VendorEventReminder]    = (NotificationPriority.Normal,   InAppWhatsAppPush),

            // Crew are field staff: WhatsApp is the channel they actually read,
            // and many have no working email address on file.
            [NotificationTemplateCodes.CrewInvitation]         = (NotificationPriority.Normal,   InAppWhatsAppPush),
            [NotificationTemplateCodes.CrewAssignment]         = (NotificationPriority.Normal,   InAppWhatsAppPush),
            [NotificationTemplateCodes.CrewAssignmentApproved] = (NotificationPriority.Normal,   InAppWhatsAppPush),
            [NotificationTemplateCodes.CrewAssignmentRejected] = (NotificationPriority.Normal,   InAppWhatsAppPush),
            [NotificationTemplateCodes.CrewInviteRevoked]      = (NotificationPriority.Normal,   InAppWhatsAppPush),
            [NotificationTemplateCodes.CrewAssignmentReminder] = (NotificationPriority.High,     InAppWhatsAppPush),

            [NotificationTemplateCodes.EventAnnouncement]      = (NotificationPriority.Normal,   AllChannelsPush),
            [NotificationTemplateCodes.EventUpdated]           = (NotificationPriority.High,     InAppWhatsAppPush),
            // Someone travelling to a cancelled event is the worst failure this
            // system can have, so it outranks ordinary traffic.
            [NotificationTemplateCodes.EventCancelled]         = (NotificationPriority.Critical, AllChannelsPush),
            [NotificationTemplateCodes.EventStarting]          = (NotificationPriority.High,     InAppWhatsAppPush),
            [NotificationTemplateCodes.ShiftChanged]           = (NotificationPriority.High,     InAppWhatsAppPush),

            [NotificationTemplateCodes.AttendanceReminder]     = (NotificationPriority.High,     InAppWhatsAppPush),
            // A receipt for something the person just did on that same phone,
            // seconds earlier. Pushing it back at them adds nothing.
            [NotificationTemplateCodes.CheckInVerified]        = (NotificationPriority.Low,      InAppOnly),

            // These six shipped after this table was written and fell through to
            // the unknown-code default of in-app only -- so they never pushed,
            // and never will unless they are listed. In-app + push only on
            // purpose: adding email or WhatsApp here would start sending
            // outbound messages that nobody has asked for, which is a separate
            // decision from switching push on.
            [NotificationTemplateCodes.RegistrationPendingApproval] = (NotificationPriority.High,   InAppPush),
            [NotificationTemplateCodes.AssignmentPendingApproval]   = (NotificationPriority.High,   InAppPush),
            [NotificationTemplateCodes.VendorAcceptedEvent]         = (NotificationPriority.Normal, InAppPush),
            [NotificationTemplateCodes.VendorRejectedEvent]         = (NotificationPriority.High,   InAppPush),
            [NotificationTemplateCodes.CrewAcceptedAssignment]      = (NotificationPriority.Normal, InAppPush),
            [NotificationTemplateCodes.CrewDeclinedAssignment]      = (NotificationPriority.High,   InAppPush),

            // Money: people chase these, and a WhatsApp line saves a phone call.
            [NotificationTemplateCodes.PaymentApproved]        = (NotificationPriority.Normal,   InAppWhatsAppPush),
            [NotificationTemplateCodes.PaymentRejected]        = (NotificationPriority.Normal,   InAppWhatsAppPush),
            [NotificationTemplateCodes.PayrollReleased]        = (NotificationPriority.Normal,   AllChannelsPush),
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
