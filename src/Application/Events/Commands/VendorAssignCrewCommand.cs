using EventWOS.Application.Interfaces;
using EventWOS.Application.Events.DTOs;
using EventWOS.Domain.Entities;
using EventWOS.Domain.Enums;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Events.Commands;

/// <summary>
/// Vendor self-service: attach one of their own crew to an event the vendor
/// has been assigned to. Used after a Manager creates a 'Vendor-only' direct
/// assignment — the vendor then staffs the event with their roster.
/// </summary>
public sealed record VendorAssignCrewCommand(
    Guid EventId,
    Guid CrewId,
    Guid VendorUserId
) : IRequest<Result<EventAssignmentDto>>;

public sealed class VendorAssignCrewHandler : IRequestHandler<VendorAssignCrewCommand, Result<EventAssignmentDto>>
{
    private readonly IAppDbContext       _db;
    private readonly IUnitOfWork         _uow;
    private readonly INotificationPusher _push;
    public VendorAssignCrewHandler(IAppDbContext db, IUnitOfWork uow, INotificationPusher push)
    {
        _db   = db;
        _uow  = uow;
        _push = push;
    }

    public async Task<Result<EventAssignmentDto>> Handle(VendorAssignCrewCommand req, CancellationToken ct)
    {
        var ev = await _db.Events.FirstOrDefaultAsync(e => e.Id == req.EventId, ct);
        if (ev is null) return Result.Failure<EventAssignmentDto>(new Error("Event.NotFound", "Event not found."));
        if (ev.Status == EventStatus.Completed || ev.Status == EventStatus.Cancelled)
            return Result.Failure<EventAssignmentDto>(new Error("Event.InvalidStatus", "Event is closed."));

        // Vendor must already be assigned to this event
        var vendorIsOnEvent = await _db.EventAssignments.AnyAsync(
            a => a.EventId  == req.EventId
              && a.VendorId == req.VendorUserId
              && a.Status   != AssignmentStatus.Declined
              && a.Status   != AssignmentStatus.RejectedByManager
              && a.Status   != AssignmentStatus.RejectedByVendor, ct);
        if (!vendorIsOnEvent)
            return Result.Failure<EventAssignmentDto>(new Error("Vendor.NotOnEvent", "You are not assigned to this event."));

        // Crew must belong to this vendor
        var crew = await _db.Users.FirstOrDefaultAsync(
            u => u.Id == req.CrewId && u.Role == UserRole.Crew && !u.IsDeleted, ct);
        if (crew is null)
            return Result.Failure<EventAssignmentDto>(new Error("Crew.NotFound", "Crew member not found."));
        if (crew.VendorId != req.VendorUserId)
            return Result.Failure<EventAssignmentDto>(new Error("Crew.NotInRoster", "That crew member is not in your roster."));

        // No double-assign
        var dup = await _db.EventAssignments.AnyAsync(
            a => a.EventId == req.EventId && a.CrewId == req.CrewId, ct);
        if (dup)
            return Result.Failure<EventAssignmentDto>(new Error("Assignment.Duplicate", "That crew is already on this event."));

        // Capacity check — placeholder vendor-only rows don't count
        if (ev.MaxCrew > 0)
        {
            var current = await _db.EventAssignments.CountAsync(
                a => a.EventId == req.EventId
                  && a.Status  != AssignmentStatus.Declined
                  && a.CrewId  != null, ct);
            if (current >= ev.MaxCrew)
                return Result.Failure<EventAssignmentDto>(new Error("Assignment.MaxReached", $"Event is fully staffed (max {ev.MaxCrew})."));
        }

        var vendor = await _db.Users.FirstOrDefaultAsync(u => u.Id == req.VendorUserId, ct);

        // Create the crew assignment (vendor self-attributed)
        var assignment = new EventAssignment(req.EventId, req.CrewId, req.VendorUserId, req.VendorUserId);
        _db.EventAssignments.Add(assignment);

        // Clean up the vendor-only placeholder row once the vendor adds real crew
        var placeholders = await _db.EventAssignments
            .Where(a => a.EventId  == req.EventId
                     && a.VendorId == req.VendorUserId
                     && a.CrewId   == null)
            .ToListAsync(ct);
        if (placeholders.Count > 0)
            _db.EventAssignments.RemoveRange(placeholders);

        await _uow.SaveChangesAsync(ct);

        await _push.PushToUserAsync(crew.Id, "AssignmentInvite", new
        {
            assignmentId = assignment.Id,
            eventTitle   = ev.Title,
            vendorName   = vendor?.FullName ?? "(vendor)",
            eventStart   = ev.StartAt
        }, ct);

        return Result.Success(new EventAssignmentDto(
            assignment.Id, ev.Id, ev.Title,
            crew.Id, crew.FullName, crew.Mobile,
            crew.DisciplineScore, crew.EventsAttended,
            crew.CrewRating, crew.CrewRatingCount,
            vendor?.Id, vendor?.FullName,
            assignment.Status.ToString(),
            assignment.RejectionReason,
            assignment.CrewRespondedAt,
            assignment.VendorReviewedAt,
            assignment.ManagerReviewedAt,
            assignment.ConfirmedAt, assignment.DeclinedAt,
            assignment.CreatedAt,
            assignment.VendorRating, assignment.RatedAt));
    }
}
