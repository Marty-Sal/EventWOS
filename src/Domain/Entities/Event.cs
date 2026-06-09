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
        int maxCrew = 0)
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
                       int currentSeatsOccupied = 0)
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
}
