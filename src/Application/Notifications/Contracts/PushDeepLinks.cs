namespace EventWOS.Application.Notifications.Contracts;

/// <summary>
/// Turns a notification into the EventWOS screen it should open.
///
/// Two rules drive the design:
///
///   * The service worker must not contain business routing. It receives a path
///     the server chose, so a route change is a backend edit rather than a new
///     service worker rollout (which users only pick up on their next visit).
///   * The path must be provably local. A notification payload that could carry
///     "https://evil.example" would turn a push into a redirect, so everything
///     here is validated to be a single-slash, scheme-less, host-less path
///     before it is allowed anywhere near a click handler.
///
/// The routes below are the real EventWOS pages -- the app has flat list routes
/// (/my-assignments, /my-payments) rather than nested detail routes, so a code
/// maps to a screen and the row is found there.
/// </summary>
public static class PushDeepLinks
{
    /// <summary>Where a notification with no better idea should land: the inbox.</summary>
    public const string Fallback = "/notifications";

    /// <summary>Longest path we will hand to a client.</summary>
    public const int MaxLength = 300;

    /// <summary>
    /// Call sites may override the destination by putting a relative path in the
    /// notification data under this key. Still validated -- an override is a
    /// convenience, not a trust boundary.
    /// </summary>
    public const string OverrideKey = "DeepLink";

    public static string For(string templateCode, IReadOnlyDictionary<string, string?>? data = null)
    {
        if (data is not null
            && data.TryGetValue(OverrideKey, out var raw)
            && TrySanitize(raw, out var overridden))
        {
            return overridden;
        }

        var code = (templateCode ?? string.Empty).Trim().ToUpperInvariant();

        return code switch
        {
            // Crew's own work: invitations, assignment outcomes, shift changes.
            NotificationTemplateCodes.CrewInvitation         or
            NotificationTemplateCodes.CrewAssignment         or
            NotificationTemplateCodes.CrewAssignmentApproved or
            NotificationTemplateCodes.CrewAssignmentRejected or
            NotificationTemplateCodes.CrewInviteRevoked      or
            NotificationTemplateCodes.CrewAssignmentReminder or
            NotificationTemplateCodes.ShiftChanged           => "/my-assignments",

            // Vendor's event invitations live on their events screen.
            NotificationTemplateCodes.VendorEventInvited  or
            NotificationTemplateCodes.VendorInviteRevoked or
            NotificationTemplateCodes.VendorEventReminder => "/my-events",

            // A vendor hearing back from their crew, on the screen where they staff.
            NotificationTemplateCodes.CrewAcceptedAssignment or
            NotificationTemplateCodes.CrewDeclinedAssignment => "/vendor-assignments",

            // Something is waiting for a decision by a manager.
            NotificationTemplateCodes.AssignmentPendingApproval => "/manager-approvals",
            NotificationTemplateCodes.RegistrationPendingApproval => "/approvals/people",

            // A vendor's answer is news for the event owner.
            NotificationTemplateCodes.VendorAcceptedEvent or
            NotificationTemplateCodes.VendorRejectedEvent => "/events",

            // Attendance.
            NotificationTemplateCodes.AttendanceReminder or
            NotificationTemplateCodes.CheckInVerified    => "/my-attendance",

            // Money.
            NotificationTemplateCodes.PaymentApproved or
            NotificationTemplateCodes.PaymentRejected or
            NotificationTemplateCodes.PayrollReleased => "/my-payments",

            // Account state.
            NotificationTemplateCodes.AccountApproved   => "/dashboard",
            NotificationTemplateCodes.ProfileCompleted  or
            NotificationTemplateCodes.AccountRejected   => "/profile",

            // The recipient is not logged in yet, so send them to the door.
            NotificationTemplateCodes.AccountInvited => "/login",

            // Announcements and event news are read in the inbox.
            _ => Fallback
        };
    }

    /// <summary>
    /// True only for a path that is unambiguously inside EventWOS. Rejects
    /// absolute URLs, protocol-relative "//host" (which browsers treat as
    /// external), backslashes, control characters and anything overlong.
    /// </summary>
    public static bool TrySanitize(string? candidate, out string path)
    {
        path = Fallback;
        if (string.IsNullOrWhiteSpace(candidate)) return false;

        var trimmed = candidate.Trim();
        if (trimmed.Length > MaxLength)                        return false;
        if (!trimmed.StartsWith('/'))                         return false;
        if (trimmed.StartsWith("//", StringComparison.Ordinal)) return false;
        if (trimmed.Contains('\\'))                           return false;
        if (trimmed.Contains(':'))                            return false; // no scheme, no "javascript:"
        if (trimmed.Any(char.IsControl))                      return false;

        path = trimmed;
        return true;
    }
}
