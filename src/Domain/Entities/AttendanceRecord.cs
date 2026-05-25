using EventWOS.Domain.Common;
using EventWOS.Domain.Enums;

namespace EventWOS.Domain.Entities;

/// <summary>
/// Records a check-in or check-out action for a crew member on an event.
/// Multiple records per assignment are allowed (re-entries etc.).
/// </summary>
public sealed class AttendanceRecord : BaseEntity
{
    private AttendanceRecord() { }

    public AttendanceRecord(
        Guid assignmentId,
        Guid eventId,
        Guid crewId,
        AttendanceAction action,
        string? location,
        string? recordedByUserId)
    {
        AssignmentId     = assignmentId;
        EventId          = eventId;
        CrewId           = crewId;
        Action           = action;
        Location         = location;
        RecordedAt       = DateTime.UtcNow;
        RecordedByUserId = recordedByUserId;
    }

    public Guid             AssignmentId     { get; private set; }
    public Guid             EventId          { get; private set; }
    public Guid             CrewId           { get; private set; }
    public AttendanceAction Action           { get; private set; }
    public DateTime         RecordedAt       { get; private set; }
    public string?          Location         { get; private set; }
    public string?          RecordedByUserId { get; private set; }

    // Navigation
    public EventAssignment Assignment { get; private set; } = default!;
    public Event           Event      { get; private set; } = default!;
    public User            Crew       { get; private set; } = default!;
}
