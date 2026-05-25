using EventWOS.Domain.Common;
using EventWOS.Domain.Enums;

namespace EventWOS.Domain.Entities;

/// <summary>
/// Assigns a Crew member to an Event via a Vendor.
/// Tracks invite → confirm/decline → attended/no-show lifecycle.
/// </summary>
public sealed class EventAssignment : BaseEntity
{
    private EventAssignment() { }

    public EventAssignment(Guid eventId, Guid crewId, Guid vendorId, Guid assignedByUserId)
    {
        EventId         = eventId;
        CrewId          = crewId;
        VendorId        = vendorId;
        AssignedByUserId = assignedByUserId;
        Status          = AssignmentStatus.Invited;
    }

    public Guid             EventId          { get; private set; }
    public Guid             CrewId           { get; private set; }
    public Guid             VendorId         { get; private set; }
    public Guid             AssignedByUserId { get; private set; }
    public AssignmentStatus Status           { get; private set; }
    public string?          Notes            { get; private set; }
    public DateTime?        ConfirmedAt      { get; private set; }
    public DateTime?        DeclinedAt       { get; private set; }

    // Navigation
    public Event Event      { get; private set; } = default!;
    public User  Crew       { get; private set; } = default!;
    public User  Vendor     { get; private set; } = default!;
    public User  AssignedBy { get; private set; } = default!;
    public ICollection<AttendanceRecord> AttendanceRecords { get; private set; } = new List<AttendanceRecord>();

    public void Confirm()
    {
        if (Status != AssignmentStatus.Invited)
            throw new InvalidOperationException("Only Invited assignments can be confirmed.");
        Status      = AssignmentStatus.Confirmed;
        ConfirmedAt = DateTime.UtcNow;
    }

    public void Decline(string? reason = null)
    {
        if (Status != AssignmentStatus.Invited)
            throw new InvalidOperationException("Only Invited assignments can be declined.");
        Status     = AssignmentStatus.Declined;
        DeclinedAt = DateTime.UtcNow;
        if (reason is not null) Notes = reason;
    }

    public void MarkAttended()  => Status = AssignmentStatus.Attended;
    public void MarkNoShow()    => Status = AssignmentStatus.NoShow;
    public void SetNotes(string notes) => Notes = notes;
}
