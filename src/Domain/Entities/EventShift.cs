using EventWOS.Domain.Common;

namespace EventWOS.Domain.Entities;

/// <summary>
/// A staffing slot on an <see cref="Event"/>. An event can have many shifts;
/// each shift represents N crew of a particular <see cref="ScopeOfWork"/>
/// (Box Office, Gates, F&amp;B…) for a specific time window.
///
/// Phase B of the Scope-of-Work feature. Replaces the per-event
/// <see cref="Event.MaxCrew"/> as the source of truth for capacity:
///   • Capacity-per-event   = SUM(shifts.CrewCount)
///   • Capacity-per-shift   = shift.CrewCount  (drives per-shift assignment)
///
/// MaxCrew stays in the schema for now (strategy "c" — see roadmap memory)
/// so legacy queries / reports keep working; it becomes a computed view in
/// a follow-up commit once every caller has migrated to shifts.
///
/// EndAt is intentionally nullable — many event types ("doors open 6pm,
/// close when the last guest leaves") genuinely don't know their finish
/// time in advance. Crew portal (Phase D) shows a "contact vendor" line
/// when EndAt is null. Vendor can optionally set a per-crew override end
/// time on the assignment in Phase C.
/// </summary>
public sealed class EventShift : BaseEntity
{
    private EventShift() { }

    public EventShift(
        Guid     eventId,
        Guid     scopeOfWorkId,
        int      crewCount,
        DateTime startAt,
        DateTime? endAt,
        Guid     createdByUserId)
    {
        if (eventId == Guid.Empty)
            throw new ArgumentException("EventId is required.", nameof(eventId));
        if (scopeOfWorkId == Guid.Empty)
            throw new ArgumentException("ScopeOfWorkId is required.", nameof(scopeOfWorkId));
        ValidateInvariants(crewCount, startAt, endAt);

        EventId         = eventId;
        ScopeOfWorkId   = scopeOfWorkId;
        CrewCount       = crewCount;
        StartAt         = startAt;
        EndAt           = endAt;
        CreatedByUserId = createdByUserId;
    }

    public Guid     EventId          { get; private set; }
    public Guid     ScopeOfWorkId    { get; private set; }
    public int      CrewCount        { get; private set; }
    public DateTime StartAt          { get; private set; }
    public DateTime? EndAt           { get; private set; }
    public Guid     CreatedByUserId  { get; private set; }

    // Navigation
    public Event       Event       { get; private set; } = default!;
    public ScopeOfWork ScopeOfWork { get; private set; } = default!;

    /// <summary>
    /// Edit the shift's editable fields. Capacity-shrink guard mirrors the
    /// one on <see cref="Event.Update"/>: cannot drop CrewCount below the
    /// number of assignments that currently occupy a seat on this shift.
    ///
    /// <paramref name="currentSeatsOccupied"/> is computed by the handler
    /// using <see cref="Rules.AssignmentCapacityRules.OccupiesSeat"/>
    /// filtered to this shift. Domain doesn't see the assignment graph
    /// directly — keeps the entity isolated from the repository.
    /// </summary>
    public void Update(int crewCount, DateTime startAt, DateTime? endAt, int currentSeatsOccupied)
    {
        ValidateInvariants(crewCount, startAt, endAt);

        if (crewCount < currentSeatsOccupied)
        {
            throw new InvalidOperationException(
                $"Cannot reduce shift capacity below {currentSeatsOccupied} — that many crew are already approved or confirmed for this shift.");
        }

        CrewCount = crewCount;
        StartAt   = startAt;
        EndAt     = endAt;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Re-point this shift at a different scope-of-work category.
    /// Separate method because it's a semantic change (not a tweak) and
    /// the handler needs to verify the new scope row isn't archived.
    /// </summary>
    public void ChangeScope(Guid newScopeOfWorkId)
    {
        if (newScopeOfWorkId == Guid.Empty)
            throw new ArgumentException("ScopeOfWorkId is required.", nameof(newScopeOfWorkId));
        ScopeOfWorkId = newScopeOfWorkId;
        UpdatedAt     = DateTime.UtcNow;
    }

    /// <summary>
    /// Soft-delete the shift. Caller MUST verify no active assignments
    /// reference it (capacity rules ban silent orphans). Handler enforces.
    /// </summary>
    public void Archive(Guid actorId, int currentSeatsOccupied)
    {
        if (currentSeatsOccupied > 0)
            throw new InvalidOperationException(
                $"Cannot delete shift — {currentSeatsOccupied} crew are already approved or confirmed on it. " +
                "Reject or unassign them first.");

        if (IsDeleted) return;            // idempotent
        IsDeleted = true;
        DeletedAt = DateTime.UtcNow;
        DeletedBy = actorId;
    }

    // ── Invariants ───────────────────────────────────────────────────────────
    private static void ValidateInvariants(int crewCount, DateTime startAt, DateTime? endAt)
    {
        if (crewCount < 1)
            throw new ArgumentException("Shift crew count must be at least 1.", nameof(crewCount));
        if (endAt.HasValue && endAt.Value <= startAt)
            throw new ArgumentException("Shift end time must be after start time.", nameof(endAt));
    }
}
