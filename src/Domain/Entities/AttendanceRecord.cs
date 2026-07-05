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
        string? locationAddress,   // Human-readable, e.g. "Airoli, Navi Mumbai".
        string? locationCoords,    // Raw "lat,lng" fix from the client, used for the map link.
        string? recordedByUserId)
    {
        AssignmentId     = assignmentId;
        EventId          = eventId;
        CrewId           = crewId;
        Action           = action;
        LocationAddress  = locationAddress;
        LocationCoords   = locationCoords;
        RecordedAt       = DateTime.UtcNow;
        RecordedByUserId = recordedByUserId;
    }

    public Guid             AssignmentId     { get; private set; }
    public Guid             EventId          { get; private set; }
    public Guid             CrewId           { get; private set; }
    public AttendanceAction Action           { get; private set; }
    public DateTime         RecordedAt       { get; private set; }

    /// <summary>
    /// Human-readable short address (e.g. "Airoli, Navi Mumbai"). Written
    /// once at check-in by <c>INominatimGeoService</c>. Nullable — geocoding
    /// may time out, be rate-limited, or the row may be a legacy pre-split row.
    /// </summary>
    public string?          LocationAddress  { get; private set; }

    /// <summary>
    /// Raw "lat,lng" (6 decimal places) from the client's
    /// navigator.geolocation fix. Powers the "View on map" link. Nullable
    /// when the browser refused location or the fix was "unavailable:*".
    /// </summary>
    public string?          LocationCoords   { get; private set; }

    public string?          RecordedByUserId { get; private set; }

    /// <summary>
    /// Backfills the address on an existing record — used by the one-shot
    /// migration that geocodes any pre-split rows whose coords were stored
    /// in the legacy <c>Location</c> column. Deliberately restricted to
    /// address-only; coords, action, timestamps, and crew are immutable.
    /// </summary>
    public void SetLocationAddress(string? address) => LocationAddress = address;

    // Navigation
    public EventAssignment Assignment { get; private set; } = default!;
    public Event           Event      { get; private set; } = default!;
    public User            Crew       { get; private set; } = default!;
}
