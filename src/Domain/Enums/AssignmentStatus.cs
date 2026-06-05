namespace EventWOS.Domain.Enums;

public enum AssignmentStatus
{
    Invited                = 0,   // Vendor sent invite, waiting for crew
    Confirmed              = 1,   // Crew accepted (legacy / direct confirm path)
    Declined               = 2,   // Crew declined OR vendor/manager rejected
    Attended               = 3,   // Crew checked in & completed event
    NoShow                 = 4,   // Crew didn't show up
    VendorApproved         = 5,   // Crew accepted → Vendor approved → waiting Manager
    PendingManagerApproval = 6,   // In Manager's approval queue
    ManagerApproved        = 7,   // Manager gave final approval → fully Confirmed
    RejectedByVendor       = 8,   // Vendor rejected crew's application
    RejectedByManager      = 9,   // Manager rejected in final review

    // ── Vendor-event invitation lifecycle (placeholder rows, CrewId == null) ──
    VendorAccepted         = 10,  // Vendor accepted Manager's invite — can now staff crew.
                                   // (Distinct from VendorApproved which is on crew rows.)
}
