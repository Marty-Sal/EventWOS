namespace EventWOS.Domain.Enums;

public enum UserStatus
{
    /// <summary>
    /// Awaiting Admin/Manager approval (self-registered Vendors and Crew start here).
    /// Cannot log in. Visible in the approval queue.
    /// </summary>
    Pending = 0,

    /// <summary>Approved and able to log in normally.</summary>
    Active = 1,

    /// <summary>Temporarily blocked by Admin. Can be Reactivated.</summary>
    Suspended = 2,

    /// <summary>Permanently disabled by Admin. Soft-archive of the account.</summary>
    Deactivated = 3,

    /// <summary>
    /// Self-registration was rejected by an Admin/Manager. Blocks re-registration
    /// with the same phone/email for 24h via RejectedAt timestamp on User.
    /// </summary>
    Rejected = 4
}
