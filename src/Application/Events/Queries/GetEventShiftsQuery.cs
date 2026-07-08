using EventWOS.Application.Events.DTOs;
using EventWOS.Application.Interfaces;
using EventWOS.Domain.Rules;
using EventWOS.Domain.Enums;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Events.Queries;

/// <summary>
/// Phase B: list all shifts on a given event, with the per-shift assigned
/// crew count baked in. Used by:
///   • Create-event modal (read-back on edit, multi-shift Phase C UI)
///   • Crew portal "what scope am I on?" header (Phase D)
///   • Vendor portal shift picker (Phase C)
///
/// Auth lives at the controller layer — we keep this query plain so it can
/// be reused from multiple endpoints. The corresponding controller action
/// must check the caller has permission to see this event.
/// </summary>
public sealed record GetEventShiftsQuery(Guid EventId) : IRequest<Result<IReadOnlyList<EventShiftDto>>>;

public sealed class GetEventShiftsHandler
    : IRequestHandler<GetEventShiftsQuery, Result<IReadOnlyList<EventShiftDto>>>
{
    private readonly IAppDbContext _db;
    public GetEventShiftsHandler(IAppDbContext db) => _db = db;

    public async Task<Result<IReadOnlyList<EventShiftDto>>> Handle(
        GetEventShiftsQuery req, CancellationToken ct)
    {
        // Two-step: pull the shifts + scope names with one join, then
        // batch-count assignments per shift in a second round trip. We
        // could collapse to a single query with a subselect per shift but
        // PG plans it poorly for events with many shifts; the two-round-
        // trip version is shape-stable.
        var shifts = await _db.EventShifts
            .Where(s => s.EventId == req.EventId)
            .Include(s => s.ScopeOfWork)
            .OrderBy(s => s.StartAt)
            .ToListAsync(ct);

        if (shifts.Count == 0)
            return Result.Success<IReadOnlyList<EventShiftDto>>(Array.Empty<EventShiftDto>());

        var shiftIds = shifts.Select(s => s.Id).ToList();

        // Two counts per shift: "assigned" (real crew only, OccupiesSeat)
        // and "reserved" (real crew + placeholder anchors, same status
        // filter as AssignmentCapacityRules.ReservesSeatOnShift).
        //
        // We build them from the same query — one .Where filters to the
        // active statuses shared by both predicates, then we branch on
        // CrewId server-side via a conditional Sum. Same round-trip cost
        // as the previous single-count version, with the added benefit
        // that the modal's "free" display will match the server's
        // capacity gate exactly.
        var counts = await _db.EventAssignments
            .Where(a => a.ShiftId != null && shiftIds.Contains(a.ShiftId.Value))
            .Where(a => !a.IsDeleted
                     && a.Status != AssignmentStatus.Declined
                     && a.Status != AssignmentStatus.RejectedByVendor
                     && a.Status != AssignmentStatus.RejectedByManager
                     && a.Status != AssignmentStatus.NoShow)
            .GroupBy(a => a.ShiftId!.Value)
            .Select(g => new
            {
                ShiftId  = g.Key,
                Assigned = g.Count(a => a.CrewId != null),
                Reserved = g.Count()
            })
            .ToListAsync(ct);

        var countsByShift = counts.ToDictionary(
            x => x.ShiftId,
            x => (Assigned: x.Assigned, Reserved: x.Reserved));

        var dtos = shifts.Select(s =>
        {
            countsByShift.TryGetValue(s.Id, out var c);
            return new EventShiftDto(
                s.Id, s.EventId, s.ScopeOfWorkId,
                s.ScopeOfWork?.Name ?? "(unknown)",
                s.CrewCount,
                c.Assigned,
                c.Reserved,
                s.StartAt, s.EndAt);
        }).ToList();

        return Result.Success<IReadOnlyList<EventShiftDto>>(dtos);
    }
}
