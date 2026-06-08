using EventWOS.Application.Events.DTOs;
using EventWOS.Application.Interfaces;
using EventWOS.Domain.Entities;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Events.Commands;

/// <summary>
/// Create an event.
///
/// Phase B contract: callers should now pass a non-empty <see cref="Shifts"/>
/// list — each entry describes one staffing slot (Box Office, Gates, etc.).
/// During the rollout the field is OPTIONAL: if the caller passes a non-empty
/// list, those shifts get created and MaxCrew is computed from their sum.
/// If they pass an empty/null list (legacy callers, tests), we fall back to
/// the old behaviour: persist with the supplied <see cref="MaxCrew"/> and
/// auto-create a single "General" shift using the seeded General scope row,
/// so the resulting event still satisfies the Phase B invariant ("every
/// event has at least one shift").
///
/// This dual-path is a deliberate, temporary scaffold. The day every caller
/// is updated, we collapse to "Shifts is required, MaxCrew goes away".
/// </summary>
public sealed record CreateEventShiftDto(
    Guid     ScopeOfWorkId,
    int      CrewCount,
    DateTime StartAt,
    DateTime? EndAt
);

public sealed record CreateEventCommand(
    string   Title,
    string?  Description,
    string   Venue,
    string?  Address,
    DateTime StartAt,
    DateTime EndAt,
    int      MaxCrew,
    Guid     CreatedByUserId,
    IReadOnlyList<CreateEventShiftDto>? Shifts = null
) : IRequest<Result<EventDto>>;

public sealed class CreateEventHandler : IRequestHandler<CreateEventCommand, Result<EventDto>>
{
    private readonly IAppDbContext _db;
    private readonly IUnitOfWork   _uow;
    public CreateEventHandler(IAppDbContext db, IUnitOfWork uow) { _db = db; _uow = uow; }

    public async Task<Result<EventDto>> Handle(CreateEventCommand req, CancellationToken ct)
    {
        // ── Resolve shifts payload ──────────────────────────────────────────
        // Two code paths converge on the same end state: an Event with >= 1
        // EventShift attached. The Phase-B-aware path validates the supplied
        // shifts; the legacy path synthesises a single "General" shift.
        var providedShifts = req.Shifts ?? Array.Empty<CreateEventShiftDto>();

        // Effective MaxCrew = sum of shift crew counts when shifts are
        // provided, otherwise the legacy value passed in MaxCrew. This is
        // what we persist into events.max_crew so legacy queries still see
        // the right number while we migrate them off the column.
        int effectiveMaxCrew = providedShifts.Count > 0
            ? providedShifts.Sum(s => s.CrewCount)
            : Math.Max(req.MaxCrew, 1);

        var ev = new Event(
            req.Title, req.Description, req.Venue, req.Address,
            req.StartAt, req.EndAt, req.CreatedByUserId,
            maxCrew: effectiveMaxCrew);

        _db.Events.Add(ev);

        // ── Build the shift rows ────────────────────────────────────────────
        if (providedShifts.Count > 0)
        {
            // Validate scope-of-work IDs in one round trip rather than N.
            // Archived rows are excluded by the global query filter, so a
            // shift referencing an archived scope will return NotFound here.
            var scopeIds = providedShifts.Select(s => s.ScopeOfWorkId).Distinct().ToList();
            var valid    = await _db.ScopesOfWork
                .Where(s => scopeIds.Contains(s.Id))
                .Select(s => s.Id)
                .ToListAsync(ct);

            var missing = scopeIds.Except(valid).ToList();
            if (missing.Count > 0)
                return Result.Failure<EventDto>(new Error(
                    "Event.InvalidScope",
                    $"Scope-of-work not found or archived: {string.Join(", ", missing)}."));

            foreach (var sh in providedShifts)
            {
                try
                {
                    var shift = new EventShift(
                        ev.Id, sh.ScopeOfWorkId, sh.CrewCount,
                        sh.StartAt, sh.EndAt, req.CreatedByUserId);
                    _db.EventShifts.Add(shift);
                }
                catch (ArgumentException ex)
                {
                    return Result.Failure<EventDto>(new Error("Event.InvalidShift", ex.Message));
                }
            }
        }
        else
        {
            // Legacy path — synthesise one "General" shift mirroring the
            // event's own (start_at, end_at, max_crew). Same shape as the
            // backfill SQL so old and new code converge on identical data.
            var general = await _db.ScopesOfWork
                .Where(s => s.Name.ToLower() == "general")
                .Select(s => s.Id)
                .FirstOrDefaultAsync(ct);

            if (general == Guid.Empty)
            {
                // Seeder hasn't run yet (extremely fresh DB). Surface a
                // clean error rather than a NULL FK explosion.
                return Result.Failure<EventDto>(new Error(
                    "Event.MissingDefaultScope",
                    "Cannot create event without shifts on a fresh DB — " +
                    "the default 'General' scope-of-work row hasn't been seeded yet."));
            }

            _db.EventShifts.Add(new EventShift(
                ev.Id, general, effectiveMaxCrew,
                req.StartAt, req.EndAt, req.CreatedByUserId));
        }

        await _uow.SaveChangesAsync(ct);

        var creator = await _db.Users.FindAsync(new object[] { req.CreatedByUserId }, ct);
        return Result.Success(MapToDto(ev, 0, creator?.FullName ?? "Unknown"));
    }

    internal static EventDto MapToDto(Domain.Entities.Event ev, int assignedCrew, string creatorName) => new(
        ev.Id, ev.Title, ev.Description, ev.Venue, ev.Address,
        ev.StartAt, ev.EndAt, ev.Status.ToString(), ev.MaxCrew,
        assignedCrew, ev.CreatedByUserId, creatorName, ev.CreatedAt);
}
