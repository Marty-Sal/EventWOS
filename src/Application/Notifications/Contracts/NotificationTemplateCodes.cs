namespace EventWOS.Application.Notifications.Contracts;

/// <summary>
/// The catalogue of notification types. Handlers reference these constants
/// instead of string literals, so a typo is a compile error rather than a
/// notification that silently never renders.
///
/// The wording for each code lives in the notification_templates table, per
/// channel -- nothing here decides what the recipient reads.
/// </summary>
public static class NotificationTemplateCodes
{
    // Onboarding / account
    public const string AccountApproved       = "ACCOUNT_APPROVED";
    public const string AccountRejected       = "ACCOUNT_REJECTED";
    public const string AccountInvited        = "ACCOUNT_INVITED";
    public const string ProfileCompleted      = "PROFILE_COMPLETED";
    public const string PasswordResetOtp      = "PASSWORD_RESET_OTP";

    /// <summary>
    /// Sent to whoever has to APPROVE a new registration, not to the applicant.
    /// Its absence is why registrations sat in the queue for days: the only signal
    /// was a toast, so an approver who was not logged in at that exact second
    /// learned nothing, and the applicant waited on a decision nobody knew to make.
    /// </summary>
    public const string RegistrationPendingApproval = "REGISTRATION_PENDING_APPROVAL";

    // Vendor lifecycle on an event
    public const string VendorEventInvited    = "VENDOR_EVENT_INVITED";
    public const string VendorInviteRevoked   = "VENDOR_INVITE_REVOKED";
    public const string VendorEventReminder   = "VENDOR_EVENT_REMINDER";

    /// <summary>
    /// To the manager who invited the vendor, when the vendor accepts the event.
    /// InApp only -- good news that needs no action beyond the queue reflecting it.
    /// </summary>
    public const string VendorAcceptedEvent   = "VENDOR_ACCEPTED_EVENT";

    /// <summary>
    /// To the manager who invited the vendor, when the vendor turns the event down.
    /// The urgent one: that staffing is now unassigned and the manager has to find
    /// another vendor before the event happens.
    /// </summary>
    public const string VendorRejectedEvent   = "VENDOR_REJECTED_EVENT";

    // Crew lifecycle on an event
    public const string CrewInvitation        = "CREW_INVITATION";
    public const string CrewAssignment        = "CREW_ASSIGNMENT";
    public const string CrewAssignmentApproved = "CREW_ASSIGNMENT_APPROVED";
    public const string CrewAssignmentRejected = "CREW_ASSIGNMENT_REJECTED";
    public const string CrewInviteRevoked     = "CREW_INVITE_REVOKED";
    public const string CrewAssignmentReminder = "CREW_ASSIGNMENT_REMINDER";

    /// <summary>
    /// Sent to Managers when a vendor-approved crew member reaches final review.
    /// Same failure as the registration queue: the only signal was a role-wide
    /// SignalR push, so work piled up unseen while the crew member waited to hear
    /// whether they had a shift.
    /// </summary>
    public const string AssignmentPendingApproval = "ASSIGNMENT_PENDING_APPROVAL";

    /// <summary>
    /// Sent to the vendor when their crew member accepts. It is an action item, not
    /// news: acceptance parks the row in the vendor's queue waiting to be forwarded
    /// to the manager, so nothing moves until the vendor looks.
    /// </summary>
    public const string CrewAcceptedAssignment = "CREW_ACCEPTED_ASSIGNMENT";

    /// <summary>
    /// Sent to the vendor when their crew member declines. The urgent one of the pair:
    /// a slot they had counted as filled is now empty and somebody has to be found
    /// before the event.
    /// </summary>
    public const string CrewDeclinedAssignment = "CREW_DECLINED_ASSIGNMENT";

    // Event operations
    public const string EventAnnouncement     = "EVENT_ANNOUNCEMENT";
    public const string EventUpdated          = "EVENT_UPDATED";
    public const string EventCancelled        = "EVENT_CANCELLED";
    public const string EventStarting         = "EVENT_STARTING";
    public const string ShiftChanged          = "SHIFT_CHANGED";

    // Attendance
    public const string AttendanceReminder    = "ATTENDANCE_REMINDER";
    public const string CheckInVerified       = "CHECK_IN_VERIFIED";

    // Money
    public const string PaymentApproved       = "PAYMENT_APPROVED";
    public const string PaymentRejected       = "PAYMENT_REJECTED";
    public const string PayrollReleased       = "PAYROLL_RELEASED";
}

/// <summary>
/// The placeholder names templates may use. Kept as constants so a template
/// author and a call site cannot drift apart: the renderer reports unknown
/// tokens, and these are the vocabulary.
/// </summary>
public static class NotificationTokens
{
    public const string RecipientName = "RecipientName";
    public const string CrewName      = "CrewName";
    public const string VendorName    = "VendorName";
    public const string ManagerName   = "ManagerName";
    public const string ActorName     = "ActorName";
    public const string EventName     = "EventName";
    public const string EventDate     = "EventDate";
    public const string EventTime     = "EventTime";
    public const string VenueName     = "VenueName";
    public const string ShiftName     = "ShiftName";
    public const string Role          = "Role";
    public const string Amount        = "Amount";
    public const string Reason        = "Reason";
    public const string Link          = "Link";
    public const string Otp           = "Otp";
    public const string Subject       = "Subject";
    public const string Message       = "Message";
}
