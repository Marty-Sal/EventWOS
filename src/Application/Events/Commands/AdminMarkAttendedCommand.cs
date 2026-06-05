using EventWOS.Application.Interfaces;
using EventWOS.Domain.Entities;
using EventWOS.Domain.Enums;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Events.Commands;

/// <summary>
/// Admin override: retroactively flip a hanging / no-show assignment to
/// Attended after the event has completed. Writes:
///   1. assignment.Status = Attended + AttendanceNote = "Missing attendance — marked attended by Admin {name} on {date}"
///   2. A synthetic AttendanceRecord with Action = AdminOverride so the
///      Attendance log shows the correction.
///
/// Caller must be Admin. Event must be Completed. The API enforces those
/// rules; the domain method enforces the source-status guard.
/// </summary>
public sealed record AdminMarkAttendedCommand(
    Guid AssignmentId,
    Guid AdminUserId
) : IRequest<Result>;

public sealed class AdminMarkAttendedHandler : IRequestHandler<AdminMarkAttendedCommand, Result>
{
    private readonly IAppDbContext _db;
    private readonly IUnitOfWork   _uow;

    public AdminMarkAttendedHandler(IAppDbContext db, IUnitOfWork uow)
    {
        _db  = db;
        _uow = uow;
    }

    public async Task<Result> Handle(AdminMarkAttendedCommand req, CancellationToken ct)
    {
        var assignment = await _db.EventAssignments
            .Include(a => a.Event)
            .FirstOrDefaultAsync(a => a.Id == req.AssignmentId && !a.IsDeleted, ct);

        if (assignment is null)
            return Result.Failure(new Error("Assignment.NotFound", "Assignment not found."));

        // Admin override only makes sense after the event is over —
        // otherwise the proper attendance flow (CheckIn) should be used.
        if (assignment.Event.Status != EventStatus.Completed)
            return Result.Failure(new Error("Event.NotCompleted",
                "Admin attendance override is only allowed on Completed events."));

        if (assignment.CrewId is null)
            return Result.Failure(new Error("Assignment.NoCrew",
                "Cannot mark a vendor-placeholder row as attended."));

        // Look up the admin's display name for the audit note.
        var admin = await _db.Users
            .Where(u => u.Id == req.AdminUserId)
            .Select(u => new { u.FullName })
            .FirstOrDefaultAsync(ct);

        var adminName = admin?.FullName ?? "System";

        try
        {
            assignment.AdminMarkAttended(req.AdminUserId, adminName);
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure(new Error("Override.Invalid", ex.Message));
        }

        // Synthetic attendance log entry so /attendance reflects the correction.
        // Action = AdminOverride distinguishes it from real CheckIn/CheckOut events.
        var record = new AttendanceRecord(
            assignmentId:     assignment.Id,
            eventId:          assignment.EventId,
            crewId:           assignment.CrewId!.Value,
            action:           AttendanceAction.AdminOverride,
            location:         assignment.AttendanceNote, // the human-readable reason
            recordedByUserId: req.AdminUserId.ToString());

        _db.AttendanceRecords.Add(record);

        await _uow.SaveChangesAsync(ct);
        return Result.Success();
    }
}
