using EventWOS.Domain.Common;
using EventWOS.Domain.Enums;
using EventWOS.Domain.Events;

namespace EventWOS.Domain.Entities;

/// <summary>
/// Core Event aggregate. Created by Admin/Manager, staffed by Vendors/Crew.
/// </summary>
public sealed class Event : BaseEntity
{
    private Event() { }

    public Event(
        string title,
        string? description,
        string venue,
        string? address,
        DateTime startAt,
        DateTime endAt,
        Guid createdByUserId,
        int maxCrew = 0,
        Guid? venueId = null)
    {
        Title           = title;
        Description     = description;
        Venue           = venue;
        Address         = address;
        StartAt         = startAt;
        EndAt           = endAt;
        CreatedByUserId = createdByUserId;
        MaxCrew         = maxCrew;
        Status          = EventStatus.Draft;
        VenueId         = venueId;
    }

    public string      Title           { get; private set; } = default!;
    public string?     Description     { get; private set; }
    public string      Venue           { get; private set; } = default!;
    public string?     Address         { get; private set; }
    public DateTime    StartAt         { get; private set; }
    public DateTime    EndAt           { get; private set; }
    public EventStatus Status          { get; private set; }
    public int         MaxCrew         { get; private set; }
    public Guid        CreatedByUserId { get; private set; }
    public string?     Notes           { get; private set; }

    /// <summary>
    /// Optional link to a catalog Venue (Settings -> Venue). When set, this
    /// event's location was chosen from the saved-venue picker rather than
    /// typed by hand -- Venue/Address above are still the display copy
    /// (denormalised at pick-time so the event keeps its own address text
    /// even if the venue is edited/archived later), but VenueId lets the
    /// UI show which venue was used and lets a future feature resolve
    /// back to the venue's lat/lng if that's ever wanted.
    /// </summary>
    public Guid?       VenueId         { get; private set; }

    // ── Attendance geofence configuration ───────────────────────────────────
    // Geofencing is deliberately configured HERE and not on Venue: two events
    // at the same venue routinely need different boundaries (a 100 m fence for
    // a single hall, 300 m for a stadium-wide festival). Venue owns the
    // confirmed physical point; Event owns the tolerance around it.
    //
    // The radius is therefore meaningless without the venue's coordinates,
    // which is why EnableGeoFence() refuses to arm a fence the server couldn't
    // actually enforce.

    /// <summary>
    /// When true, attendance check-in must pass a server-side distance check
    /// against the linked Venue's coordinates. False = location is recorded for
    /// the audit trail but never blocks a check-in.
    /// </summary>
    public bool        GeoFenceEnabled { get; private set; }

    /// <summary>
    /// Permitted distance in metres from the Venue's coordinates. Null when the
    /// fence is off. This is the ONLY radius the attendance path trusts — a
    /// radius sent by a client is ignored.
    /// </summary>
    public int?        GeoFenceRadiusMeters { get; private set; }

    /// <summary>
    /// Floor of 20 m: consumer GPS is typically accurate to 5-20 m, so a
    /// tighter fence would reject crew who are genuinely standing on site.
    /// </summary>
    public const int MinGeoFenceRadiusMeters = 20;

    /// <summary>
    /// Ceiling of 5 km. Beyond this the fence stops being a presence check;
    /// the cap also stops a typo (500000) from silently disabling enforcement.
    /// </summary>
    public const int MaxGeoFenceRadiusMeters = 5_000;

    // Navigation
    public User                        Creator     { get; private set; } = default!;
    public ICollection<EventAssignment> Assignments { get; private set; } = new List<EventAssignment>();
    /// <summary>
    /// Phase B (Scope-of-Work): staffing breakdown of this event as one or
    /// more <see cref="EventShift"/> rows. The sum of <see cref="EventShift.CrewCount"/>
    /// is the authoritative staffing cap; the legacy <see cref="MaxCrew"/>
    /// field stays in the schema during the rollout but is no longer the
    /// source of truth (strategy "c", see Phase B/C/D roadmap).
    /// </summary>
    public ICollection<EventShift>      Shifts      { get; private set; } = new List<EventShift>();

    // ── Behaviours ────────────────────────────────────────────────────────────
    public void Publish()
    {
        if (Status != EventStatus.Draft)
            throw new InvalidOperationException("Only Draft events can be published.");
        Status = EventStatus.Published;
    }

    public void Start()
    {
        // Phase D step 21: the admin lifecycle was previously
        // Draft → Publish → Start → Complete (four buttons). Field admins
        // told us the "Publish" step added zero value: every Draft event
        // they create is meant to go live; the manual Publish step was
        // just a click tax. We now collapse Draft+Published into a single
        // "Start" transition. The Published state is retained in the
        // enum because dashboards / analytics still report on it (e.g.
        // "upcoming active events" = Published + InProgress), and we
        // pass through it for one tick of state to keep the audit trail
        // accurate, then immediately progress to InProgress.
        if (Status == EventStatus.Draft)
            Status = EventStatus.Published; // transparent intermediate hop
        if (Status != EventStatus.Published)
            throw new InvalidOperationException("Only Draft or Published events can be started.");
        Status = EventStatus.InProgress;
    }

    public void Complete()
    {
        if (Status != EventStatus.InProgress)
            throw new InvalidOperationException("Only InProgress events can be completed.");
        Status = EventStatus.Completed;
        AddDomainEvent(new EventCompletedEvent(Id));
    }

    public void Cancel(string? reason = null)
    {
        if (Status == EventStatus.Completed || Status == EventStatus.Cancelled)
            throw new InvalidOperationException("Event cannot be cancelled.");
        Status = EventStatus.Cancelled;
        if (reason is not null) Notes = reason;
    }

    /// <summary>
    /// Update editable fields on the event.
    ///
    /// <paramref name="currentSeatsOccupied"/> is the count of EventAssignments
    /// that currently OccupiesSeat (see <c>AssignmentCapacityRules</c>). The
    /// handler computes this — the domain entity doesn't see the assignment
    /// graph directly, so we pass it in. This keeps the rule colocated with
    /// the invariant it protects (you can't shrink MaxCrew below already-
    /// approved staff) without coupling the entity to a repository.
    /// </summary>
    public void Update(string title, string? description, string venue, string? address,
                       DateTime startAt, DateTime endAt, int maxCrew,
                       int currentSeatsOccupied = 0,
                       Guid? venueId = null)
    {
        if (Status == EventStatus.Completed || Status == EventStatus.Cancelled)
            throw new InvalidOperationException("Completed or Cancelled events cannot be edited.");

        // Guard: you cannot shrink the staffing cap below the number of crew
        // who already occupy a seat (approved / confirmed / attended). The UI
        // shows the floor when editing, but a determined client could still
        // POST a smaller value — so the rule lives here too.
        // MaxCrew == 0 historically means "unlimited", so skip the check then.
        if (maxCrew > 0 && maxCrew < currentSeatsOccupied)
        {
            throw new InvalidOperationException(
                $"Cannot reduce staff cap below {currentSeatsOccupied} — that many crew are already approved or confirmed for this event.");
        }

        Title       = title;
        Description = description;
        Venue       = venue;
        Address     = address;
        StartAt     = startAt;
        EndAt       = endAt;
        MaxCrew     = maxCrew;
        VenueId     = venueId;
        UpdatedAt   = DateTime.UtcNow;
    }

    /// <summary>
    /// Recompute capacity from the event's shifts. Called by shift
    /// add/update/archive handlers after they mutate a shift, so the
    /// legacy MaxCrew field always equals SUM(active shifts.CrewCount).
    /// Auto-grow only — see argument <paramref name="currentSeatsOccupied"/>
    /// for the shrink guard. Handlers pass the sum from the database.
    /// </summary>
    public void RecomputeCapacityFromShifts(int newTotal, int currentSeatsOccupied = 0)
    {
        if (newTotal < 0)
            throw new ArgumentOutOfRangeException(nameof(newTotal), "Capacity cannot be negative.");
        if (newTotal < currentSeatsOccupied)
            throw new InvalidOperationException(
                $"Cannot reduce capacity below {currentSeatsOccupied} — that many crew already occupy a seat.");

        MaxCrew   = newTotal;
        UpdatedAt = DateTime.UtcNow;
    }

    // ── Geofence behaviour ──────────────────────────────────────────────────

    /// <summary>
    /// Arm the attendance geofence at <paramref name="radiusMeters"/>.
    ///
    /// <paramref name="venueHasCoordinates"/> is passed in by the handler
    /// (which has the Venue loaded) rather than read off a navigation property,
    /// so the invariant holds even when the aggregate is used detached. The
    /// check exists because an armed fence with no venue coordinates is
    /// unenforceable: the attendance path would have nothing to measure from and
    /// would have to either fail every check-in or wave everyone through. Both
    /// are worse than refusing to save the configuration.
    /// </summary>
    public void EnableGeoFence(int radiusMeters, bool venueHasCoordinates)
    {
        if (VenueId is null)
            throw new InvalidOperationException(
                "Select a saved venue before enabling location verification — the geofence is measured from the venue's coordinates.");

        if (!venueHasCoordinates)
            throw new InvalidOperationException(
                "This venue has no coordinates saved yet. Set them in Settings → Venue (search for the place and confirm the pin) before enabling location verification.");

        if (radiusMeters < MinGeoFenceRadiusMeters || radiusMeters > MaxGeoFenceRadiusMeters)
            throw new ArgumentOutOfRangeException(
                nameof(radiusMeters),
                $"Geofence radius must be between {MinGeoFenceRadiusMeters} and {MaxGeoFenceRadiusMeters} metres.");

        GeoFenceEnabled      = true;
        GeoFenceRadiusMeters = radiusMeters;
        UpdatedAt            = DateTime.UtcNow;
    }

    /// <summary>
    /// Disarm the fence. Clears the radius too so a disabled fence can never
    /// leave a stale number behind for someone to misread as active.
    /// </summary>
    public void DisableGeoFence()
    {
        GeoFenceEnabled      = false;
        GeoFenceRadiusMeters = null;
        UpdatedAt            = DateTime.UtcNow;
    }
}
