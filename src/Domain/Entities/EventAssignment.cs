using EventWOS.Domain.Common;
using EventWOS.Domain.Enums;

namespace EventWOS.Domain.Entities;

/// <summary>
/// Assigns a Crew member to an Event via a Vendor.
/// Full 2-step lifecycle: Invited → CrewConfirmed → VendorApproved →
/// PendingManagerApproval → ManagerApproved (Confirmed) / RejectedByVendor / RejectedByManager
/// </summary>
public sealed class EventAssignment : BaseEntity
{
    private EventAssignment() { }

    public EventAssignment(Guid eventId, Guid? crewId, Guid? vendorId, Guid assignedByUserId)
    {
        EventId          = eventId;
        CrewId           = crewId;
        VendorId         = vendorId;
        AssignedByUserId = assignedByUserId;
        Status           = AssignmentStatus.Invited;
    }

    public Guid             EventId           { get; private set; }
    public Guid?            CrewId            { get; private set; }
    public Guid?            VendorId          { get; private set; }
    public Guid             AssignedByUserId  { get; private set; }
    public AssignmentStatus Status            { get; private set; }
    public string?          Notes             { get; private set; }

    // Step timestamps
    public DateTime?        CrewRespondedAt   { get; private set; }
    public DateTime?        VendorReviewedAt  { get; private set; }
    public DateTime?        ManagerReviewedAt { get; private set; }

    // Legacy compat
    public DateTime?        ConfirmedAt       { get; private set; }
    public DateTime?        DeclinedAt        { get; private set; }

    // Rejection tracking
    public string?          RejectionReason   { get; private set; }
    public Guid?            RejectedByUserId  { get; private set; }

    // Vendor crew rating (set after event attendance)
    public decimal?  VendorRating  { get; private set; }  // 1–5 stars
    public DateTime? RatedAt       { get; private set; }

    // Navigation
    public Event  Event       { get; private set; } = default!;
    public User?  Crew        { get; private set; }
    public User?  Vendor      { get; private set; }
    public User   AssignedBy  { get; private set; } = default!;
    public ICollection<AttendanceRecord> AttendanceRecords { get; private set; } = new List<AttendanceRecord>();

    // ── Step 1: Crew responds to invitation ──────────────────────────────────

    /// <summary>
    /// Crew accepts the invitation.
    /// - If assignment has a vendor: → VendorApproved (waiting for vendor to forward to manager).
    /// - If assignment is direct (no vendor): → PendingManagerApproval (skip vendor step entirely).
    /// </summary>
    public void CrewAccept()
    {
        if (Status != AssignmentStatus.Invited)
            throw new InvalidOperationException("Only Invited assignments can be accepted by crew.");

        if (VendorId is null)
        {
            // Direct-assignment flow — skip vendor, straight to manager queue
            Status              = AssignmentStatus.PendingManagerApproval;
            VendorReviewedAt    = DateTime.UtcNow; // mark vendor step as "auto-passed"
        }
        else
        {
            Status              = AssignmentStatus.VendorApproved;
        }
        CrewRespondedAt  = DateTime.UtcNow;
        ConfirmedAt      = DateTime.UtcNow; // legacy compat
    }

    /// <summary>Crew declines the invitation.</summary>
    public void CrewDecline(string? reason = null)
    {
        if (Status != AssignmentStatus.Invited)
            throw new InvalidOperationException("Only Invited assignments can be declined by crew.");
        Status           = AssignmentStatus.Declined;
        CrewRespondedAt  = DateTime.UtcNow;
        DeclinedAt       = DateTime.UtcNow;
        if (reason is not null) Notes = reason;
    }

    // ── Step 2: Vendor reviews crew acceptance ────────────────────────────────

    /// <summary>Vendor approves crew → moves to Manager approval queue.</summary>
    public void VendorApprove()
    {
        if (Status != AssignmentStatus.VendorApproved)
            throw new InvalidOperationException("Only VendorApproved assignments can be forwarded to Manager.");
        Status              = AssignmentStatus.PendingManagerApproval;
        VendorReviewedAt    = DateTime.UtcNow;
    }

    /// <summary>
    /// Vendor directly forwards an Invited crew member to Manager approval,
    /// skipping the crew acceptance step. Used when crew confirmation happened offline.
    /// </summary>
    public void VendorDirectForward()
    {
        if (Status != AssignmentStatus.Invited && Status != AssignmentStatus.VendorApproved)
            throw new InvalidOperationException("Only Invited or VendorApproved assignments can be directly forwarded.");
        Status           = AssignmentStatus.PendingManagerApproval;
        CrewRespondedAt  ??= DateTime.UtcNow; // mark as responded if not already
        VendorReviewedAt = DateTime.UtcNow;
        ConfirmedAt      ??= DateTime.UtcNow;
    }

    /// <summary>Vendor rejects crew — rejection reason is mandatory.</summary>
    public void VendorReject(Guid rejectedByUserId, string reason)
    {
        if (Status != AssignmentStatus.VendorApproved)
            throw new InvalidOperationException("Only VendorApproved assignments can be rejected by vendor.");
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("Rejection reason is mandatory.", nameof(reason));
        Status             = AssignmentStatus.RejectedByVendor;
        VendorReviewedAt   = DateTime.UtcNow;
        RejectionReason    = reason;
        RejectedByUserId   = rejectedByUserId;
        DeclinedAt         = DateTime.UtcNow;
    }

    // ── Step 3: Manager final decision ───────────────────────────────────────

    /// <summary>Manager gives final approval → assignment is fully Confirmed.</summary>
    public void ManagerApprove()
    {
        if (Status != AssignmentStatus.PendingManagerApproval)
            throw new InvalidOperationException("Only PendingManagerApproval assignments can be approved by manager.");
        Status              = AssignmentStatus.ManagerApproved;
        ManagerReviewedAt   = DateTime.UtcNow;
    }

    /// <summary>Manager rejects in final review — rejection reason is mandatory.</summary>
    public void ManagerReject(Guid rejectedByUserId, string reason)
    {
        if (Status != AssignmentStatus.PendingManagerApproval)
            throw new InvalidOperationException("Only PendingManagerApproval assignments can be rejected by manager.");
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("Rejection reason is mandatory.", nameof(reason));
        Status              = AssignmentStatus.RejectedByManager;
        ManagerReviewedAt   = DateTime.UtcNow;
        RejectionReason     = reason;
        RejectedByUserId    = rejectedByUserId;
        DeclinedAt          = DateTime.UtcNow;
    }

    // ── Attendance ───────────────────────────────────────────────────────────
    public void MarkAttended() => Status = AssignmentStatus.Attended;
    public void MarkNoShow()   => Status = AssignmentStatus.NoShow;
    public void SetNotes(string notes) => Notes = notes;

    // ── Rating ────────────────────────────────────────────────────────────────
    /// <summary>Vendor rates this crew member 1–5 stars. Can only be called once per assignment.</summary>
    public void RateCrewMember(decimal stars)
    {
        if (Status != AssignmentStatus.Attended)
            throw new InvalidOperationException("Can only rate crew after they have attended.");
        if (VendorRating.HasValue)
            throw new InvalidOperationException("This crew member has already been rated for this assignment.");
        if (stars < 1 || stars > 5)
            throw new ArgumentOutOfRangeException(nameof(stars), "Rating must be between 1 and 5.");
        VendorRating = stars;
        RatedAt      = DateTime.UtcNow;
    }

    // ── Backward-compat helpers ──────────────────────────────────────────────
    /// <summary>True if this assignment is in a fully active/confirmed state (eligible for attendance).</summary>
    public bool IsEligibleForAttendance =>
        Status is AssignmentStatus.ManagerApproved or AssignmentStatus.Confirmed;
}
