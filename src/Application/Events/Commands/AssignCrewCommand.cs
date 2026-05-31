using EventWOS.Application.Interfaces;
using EventWOS.Application.Events.DTOs;
using EventWOS.Domain.Entities;
using EventWOS.Domain.Enums;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;
using EventWOS.Domain.Rules;

namespace EventWOS.Application.Events.Commands;

public sealed record AssignCrewCommand(
    Guid EventId,
    Guid? CrewId,
    Guid? VendorId,
    Guid AssignedByUserId
) : IRequest<Result<EventAssignmentDto>>;

public sealed class AssignCrewHandler : IRequestHandler<AssignCrewCommand, Result<EventAssignmentDto>>
{
    private readonly IAppDbContext       _db;
    private readonly IUnitOfWork         _uow;
    private readonly INotificationPusher _push;
    public AssignCrewHandler(IAppDbContext db, IUnitOfWork uow, INotificationPusher push)
    {
        _db   = db;
        _uow  = uow;
        _push = push;
    }

    public async Task<Result<EventAssignmentDto>> Handle(AssignCrewCommand req, CancellationToken ct)
    {
        // Validate at least one of crew/vendor is set
        if (req.CrewId is null && req.VendorId is null)
            return Result.Failure<EventAssignmentDto>(new Error("Assignment.Empty", "Provide a vendor, a crew member, or both."));

        var ev = await _db.Events.FirstOrDefaultAsync(e => e.Id == req.EventId, ct);
        if (ev is null) return Result.Failure<EventAssignmentDto>(new Error("Event.NotFound", "Event not found."));
        if (ev.Status == EventStatus.Completed || ev.Status == EventStatus.Cancelled)
            return Result.Failure<EventAssignmentDto>(new Error("Event.InvalidStatus", "Cannot assign crew to completed/cancelled events."));

        User? crew = null;
        if (req.CrewId.HasValue)
        {
            crew = await _db.Users.FirstOrDefaultAsync(u => u.Id == req.CrewId.Value && u.Role == UserRole.Crew, ct);
            if (crew is null) return Result.Failure<EventAssignmentDto>(new Error("Crew.NotFound", "Crew member not found."));
        }

        User? vendor = null;
        if (req.VendorId.HasValue)
        {
            vendor = await _db.Users.FirstOrDefaultAsync(u => u.Id == req.VendorId.Value && u.Role == UserRole.Vendor, ct);
            if (vendor is null) return Result.Failure<EventAssignmentDto>(new Error("Vendor.NotFound", "Vendor not found."));
        }

        // Duplicate check (only meaningful when a crew member is specified)
        if (req.CrewId.HasValue)
        {
            var exists = await _db.EventAssignments.AnyAsync(
                a => a.EventId == req.EventId && a.CrewId == req.CrewId, ct);
            if (exists) return Result.Failure<EventAssignmentDto>(new Error("Assignment.Duplicate", "Crew already assigned to this event."));
        }

        // Check max crew — only count rows that genuinely occupy a seat
        // (real crew, not declined/rejected/no-show, not placeholder).
        if (ev.MaxCrew > 0)
        {
            var current = await _db.EventAssignments
                .Where(a => a.EventId == req.EventId)
                .Where(AssignmentCapacityRules.OccupiesSeat)
                .CountAsync(ct);
            if (current >= ev.MaxCrew)
                return Result.Failure<EventAssignmentDto>(new Error("Assignment.MaxReached", $"Event is fully staffed (max {ev.MaxCrew})."));
        }

        var assignment = new EventAssignment(req.EventId, req.CrewId, req.VendorId, req.AssignedByUserId);
        _db.EventAssignments.Add(assignment);
        await _uow.SaveChangesAsync(ct);

        // Push notifications
        if (crew is not null)
        {
            // Crew gets invited
            await _push.PushToUserAsync(crew.Id, "AssignmentInvite", new
            {
                assignmentId = assignment.Id,
                eventTitle   = ev.Title,
                vendorName   = vendor?.FullName ?? "Manager (direct)",
                eventStart   = ev.StartAt
            }, ct);
        }
        else if (vendor is not null)
        {
            // Vendor-only: notify vendor that they need to staff this event
            await _push.PushToUserAsync(vendor.Id, "VendorEventAssigned", new
            {
                assignmentId = assignment.Id,
                eventTitle   = ev.Title,
                eventStart   = ev.StartAt
            }, ct);
        }

        return Result.Success(new EventAssignmentDto(
            assignment.Id, ev.Id, ev.Title,
            crew?.Id ?? Guid.Empty,
            crew?.FullName ?? "(vendor to fill)",
            crew?.Mobile   ?? "",
            crew?.DisciplineScore ?? 0,
            crew?.EventsAttended  ?? 0,
            crew?.CrewRating,
            crew?.CrewRatingCount ?? 0,
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
