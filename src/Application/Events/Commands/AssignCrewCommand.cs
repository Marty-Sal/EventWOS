using EventWOS.Application.Interfaces;
using EventWOS.Application.Events.DTOs;
using EventWOS.Domain.Entities;
using EventWOS.Domain.Enums;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Events.Commands;

public sealed record AssignCrewCommand(
    Guid EventId,
    Guid CrewId,
    Guid VendorId,
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
        var ev = await _db.Events.FirstOrDefaultAsync(e => e.Id == req.EventId, ct);
        if (ev is null) return Result.Failure<EventAssignmentDto>(new Error("Event.NotFound", "Event not found."));
        if (ev.Status == EventStatus.Completed || ev.Status == EventStatus.Cancelled)
            return Result.Failure<EventAssignmentDto>(new Error("Event.InvalidStatus", "Cannot assign crew to completed/cancelled events."));

        var crew = await _db.Users.FirstOrDefaultAsync(u => u.Id == req.CrewId && u.Role == UserRole.Crew, ct);
        if (crew is null) return Result.Failure<EventAssignmentDto>(new Error("Crew.NotFound", "Crew member not found."));

        var vendor = await _db.Users.FirstOrDefaultAsync(u => u.Id == req.VendorId && u.Role == UserRole.Vendor, ct);
        if (vendor is null) return Result.Failure<EventAssignmentDto>(new Error("Vendor.NotFound", "Vendor not found."));

        // Check duplicate
        var exists = await _db.EventAssignments.AnyAsync(
            a => a.EventId == req.EventId && a.CrewId == req.CrewId, ct);
        if (exists) return Result.Failure<EventAssignmentDto>(new Error("Assignment.Duplicate", "Crew already assigned to this event."));

        // Check max crew
        if (ev.MaxCrew > 0)
        {
            var current = await _db.EventAssignments.CountAsync(
                a => a.EventId == req.EventId && a.Status != AssignmentStatus.Declined, ct);
            if (current >= ev.MaxCrew)
                return Result.Failure<EventAssignmentDto>(new Error("Assignment.MaxReached", $"Event is fully staffed (max {ev.MaxCrew})."));
        }

        var assignment = new EventAssignment(req.EventId, req.CrewId, req.VendorId, req.AssignedByUserId);
        _db.EventAssignments.Add(assignment);
        await _uow.SaveChangesAsync(ct);

        // Notify crew of their new invitation
        await _push.PushToUserAsync(crew.Id, "AssignmentInvite", new
        {
            assignmentId = assignment.Id,
            eventTitle   = ev.Title,
            vendorName   = vendor.FullName,
            eventStart   = ev.StartAt
        }, ct);

        return Result.Success(new EventAssignmentDto(
            assignment.Id, ev.Id, ev.Title,
            crew.Id, crew.FullName, crew.Mobile,
            crew.DisciplineScore, crew.EventsAttended,
            crew.CrewRating, crew.CrewRatingCount,
            vendor.Id, vendor.FullName,
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
