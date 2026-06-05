using EventWOS.Application.Interfaces;
using EventWOS.Domain.Enums;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Payments.Queries;

/// <summary>
/// Returns every payable line for an event: every vendor that had attended crew on
/// the event, plus every direct-assigned crew member who attended. Used by the
/// event-centric "New Payroll Batch" dialog so Admin/Manager can type per-row
/// amounts and approve everything in one shot.
/// </summary>
public sealed record GetEventPayableRosterQuery(Guid EventId) : IRequest<Result<EventPayableRosterDto>>;

public sealed record EventPayableRosterDto(
    Guid    EventId,
    string  EventTitle,
    string  EventStatus,
    DateTime EventStartAt,
    IReadOnlyList<PayableLineDto> VendorLines,
    IReadOnlyList<PayableLineDto> DirectCrewLines
);

public sealed record PayableLineDto(
    string  Kind,              // "Vendor" | "DirectCrew"
    Guid    PartyId,           // VendorId or CrewId
    string  PartyName,
    string  PartyMobile,
    int     AttendedCrewCount, // for Vendor lines, how many of their crew attended
    decimal SuggestedAmount,   // sum of agreed amounts on existing assignments (0 if none)
    bool    AlreadyHasPayment  // true if any non-rejected payment already exists for this party on this event (batched or not)
);

public sealed class GetEventPayableRosterHandler
    : IRequestHandler<GetEventPayableRosterQuery, Result<EventPayableRosterDto>>
{
    private readonly IAppDbContext _db;
    public GetEventPayableRosterHandler(IAppDbContext db) => _db = db;

    public async Task<Result<EventPayableRosterDto>> Handle(
        GetEventPayableRosterQuery q, CancellationToken ct)
    {
        var ev = await _db.Events
            .Where(e => e.Id == q.EventId)
            .Select(e => new { e.Id, e.Title, e.Status, e.StartAt })
            .FirstOrDefaultAsync(ct);

        if (ev is null)
            return Result.Failure<EventPayableRosterDto>(Error.Custom("Event.NotFound", "Event not found."));

        // Only attended crew count as payable. Pull all CheckIn records for the event.
        var attendedAssignmentIds = await _db.AttendanceRecords
            .Where(r => r.EventId == q.EventId && r.Action == AttendanceAction.CheckIn)
            .Select(r => r.AssignmentId)
            .Distinct()
            .ToListAsync(ct);

        if (attendedAssignmentIds.Count == 0)
        {
            return Result.Success(new EventPayableRosterDto(
                ev.Id, ev.Title, ev.Status.ToString(), ev.StartAt,
                Array.Empty<PayableLineDto>(), Array.Empty<PayableLineDto>()));
        }

        // Active (non-rejected) assignments that actually attended.
        var rejected = new[]
        {
            AssignmentStatus.Declined,
            AssignmentStatus.RejectedByVendor,
            AssignmentStatus.RejectedByManager,
            AssignmentStatus.NoShow
        };

        var assignments = await _db.EventAssignments
            .Where(a => a.EventId == q.EventId
                     && a.CrewId  != null
                     && !rejected.Contains(a.Status)
                     && attendedAssignmentIds.Contains(a.Id))
            .Select(a => new
            {
                a.Id,
                a.VendorId,
                a.CrewId,
                CrewName   = a.Crew!.FullName,
                CrewMobile = a.Crew!.Mobile,
                VendorName = a.Vendor == null ? null : a.Vendor.FullName,
                VendorMobile = a.Vendor == null ? null : a.Vendor.Mobile
            })
            .ToListAsync(ct);

        // Existing non-rejected payment rows so we can grey out parties already
        // covered. We count BOTH batched and un-batched payments — once a row
        // exists for a vendor/crew on this event, the manager shouldn't add a
        // second one from the "New Payroll Batch" dialog. (Auto-batched ad-hoc
        // payments created via "+ New Payment" land here too.)
        var existingPayments = await _db.CrewPayments
            .Where(p => p.EventId == q.EventId
                     && p.Status != PaymentStatus.Rejected)
            .Select(p => new { p.VendorId, p.CrewId })
            .ToListAsync(ct);
        var vendorHasPayment = existingPayments
            .Where(x => x.VendorId.HasValue)
            .Select(x => x.VendorId!.Value).Distinct().ToHashSet();
        var crewHasPayment   = existingPayments
            .Select(x => x.CrewId).Distinct().ToHashSet();

        // ── Vendor lines: group attended crew by vendor (non-null VendorId)
        var vendorLines = assignments
            .Where(a => a.VendorId.HasValue)
            .GroupBy(a => new { VendorId = a.VendorId!.Value, a.VendorName, a.VendorMobile })
            .Select(g => new PayableLineDto(
                "Vendor",
                g.Key.VendorId,
                g.Key.VendorName ?? "(unknown vendor)",
                g.Key.VendorMobile ?? "",
                g.Count(),
                0m,
                vendorHasPayment.Contains(g.Key.VendorId)))
            .OrderBy(l => l.PartyName)
            .ToList();

        // ── Direct crew lines: assignments with no vendor
        var directLines = assignments
            .Where(a => !a.VendorId.HasValue)
            .Select(a => new PayableLineDto(
                "DirectCrew",
                a.CrewId!.Value,
                a.CrewName,
                a.CrewMobile,
                1,
                0m,
                crewHasPayment.Contains(a.CrewId!.Value)))
            .OrderBy(l => l.PartyName)
            .ToList();

        return Result.Success(new EventPayableRosterDto(
            ev.Id, ev.Title, ev.Status.ToString(), ev.StartAt,
            vendorLines, directLines));
    }
}
