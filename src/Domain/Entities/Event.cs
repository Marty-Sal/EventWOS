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
    public User                        CreatedBy   { get; private set; } = default!;
    public ICollection<EventAssignment> Assignments { get; private set; } = new List<EventAssignment>();

    // ── Behaviours ────────────────────────────────────────────────────────────
    public void Publish()
    {
        if (Status != EventStatus.Draft)
            throw new InvalidOperationException("Only Draft events can be published.");
        Status = EventStatus.Published;
    }

    public void Start()
    {
        if (Status != EventStatus.Published)
            throw new InvalidOperationException("Only Published events can be started.");
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

    public void Update(string title, string? description, string venue, string? address,
                       DateTime startAt, DateTime endAt, int maxCrew)
    {
        if (Status == EventStatus.Completed || Status == EventStatus.Cancelled)
            throw new InvalidOperationException("Completed or Cancelled events cannot be edited.");
        Title       = title;
        Description = description;
        Venue       = venue;
        Address     = address;
        StartAt     = startAt;
        EndAt       = endAt;
        MaxCrew     = maxCrew;
        UpdatedAt   = DateTime.UtcNow;
    }
}
