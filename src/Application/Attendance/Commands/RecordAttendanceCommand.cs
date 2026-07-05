using EventWOS.Application.Events.DTOs;
using EventWOS.Application.Attendance.DTOs;
using EventWOS.Application.Interfaces;
using EventWOS.Application.Attendance.Geo;
using EventWOS.Domain.Entities;
using EventWOS.Domain.Enums;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Attendance.Commands;

public sealed record RecordAttendanceCommand(
    Guid   AssignmentId,
    string Action,        // "checkin" | "checkout"
    string? Location,
    string? RecordedByUserId
) : IRequest<Result<AttendanceRecordDto>>;

public sealed class RecordAttendanceHandler : IRequestHandler<RecordAttendanceCommand, Result<AttendanceRecordDto>>
{
    private readonly IAppDbContext _db;
    private readonly IUnitOfWork   _uow;
    private readonly IGeoLocationService _geo;
    public RecordAttendanceHandler(IAppDbContext db, IUnitOfWork uow, IGeoLocationService geo)
    {
        _db  = db; _uow = uow; _geo = geo;
    }

    public async Task<Result<AttendanceRecordDto>> Handle(RecordAttendanceCommand req, CancellationToken ct)
    {
        var assignment = await _db.EventAssignments
            .Include(a => a.Event)
            .Include(a => a.Crew)
            .FirstOrDefaultAsync(a => a.Id == req.AssignmentId, ct);

        if (assignment is null)
            return Result.Failure<AttendanceRecordDto>(new Error("Assignment.NotFound", "Assignment not found."));
        if (assignment.Event.Status != EventStatus.InProgress)
            return Result.Failure<AttendanceRecordDto>(new Error("Event.NotInProgress", "Attendance can only be recorded for InProgress events."));

        AttendanceAction action;
        if (req.Action.Equals("checkin", StringComparison.OrdinalIgnoreCase))
            action = AttendanceAction.CheckIn;
        else if (req.Action.Equals("checkout", StringComparison.OrdinalIgnoreCase))
            action = AttendanceAction.CheckOut;
        else
            return Result.Failure<AttendanceRecordDto>(new Error("Attendance.InvalidAction", "Action must be 'checkin' or 'checkout'."));

        if (assignment.CrewId is null)
            return Result.Failure<AttendanceRecordDto>(new Error("Attendance.NoCrew", "Cannot record attendance — this assignment has no crew member yet."));

        // ── Guard against duplicate / out-of-order attendance actions ────────
        var existingActions = await _db.AttendanceRecords
            .Where(r => r.AssignmentId == assignment.Id)
            .Select(r => r.Action)
            .ToListAsync(ct);

        var alreadyCheckedIn  = existingActions.Contains(AttendanceAction.CheckIn);
        var alreadyCheckedOut = existingActions.Contains(AttendanceAction.CheckOut);

        if (action == AttendanceAction.CheckIn && alreadyCheckedIn)
            return Result.Failure<AttendanceRecordDto>(new Error(
                "Attendance.AlreadyCheckedIn",
                "You have already checked in for this event."));

        if (action == AttendanceAction.CheckOut && !alreadyCheckedIn)
            return Result.Failure<AttendanceRecordDto>(new Error(
                "Attendance.NotCheckedIn",
                "You must check in before you can check out."));

        if (action == AttendanceAction.CheckOut && alreadyCheckedOut)
            return Result.Failure<AttendanceRecordDto>(new Error(
                "Attendance.AlreadyCheckedOut",
                "You have already checked out for this event."));

        // Enrich "lat,lng" → "lat,lng|City, State, Country" using the
        // embedded GeoNames dataset. See RecordAttendanceHandler for the
        // matching enrichment on QR-verified check-ins.
        var enrichedLocation = _geo.Enrich(req.Location);
        var record = new AttendanceRecord(
            assignment.Id, assignment.EventId, assignment.CrewId.Value,
            action, enrichedLocation, req.RecordedByUserId);

        _db.AttendanceRecords.Add(record);

        // Auto-update assignment status on check-in
        if (action == AttendanceAction.CheckIn && assignment.IsEligibleForAttendance)
            assignment.MarkAttended();

        // ── Check-OUT: update discipline score + events attended ──────────────
        if (action == AttendanceAction.CheckOut)
        {
            var crewUser = assignment.Crew;

            // Increment events attended (each completed checkout = 1 event)
            crewUser.IncrementEventsAttended();

            // Recalculate discipline score from attendance history for this crew
            // Score formula: (attended checkouts / total confirmed assignments) * 100
            // Weighted: existing score * 0.7 + new event contribution * 0.3
            var totalAssignments = await _db.EventAssignments
                .CountAsync(a => a.CrewId == crewUser.Id
                              && !a.IsDeleted
                              && a.Status != AssignmentStatus.Invited
                              && a.Status != AssignmentStatus.Declined
                              && a.Status != AssignmentStatus.RejectedByVendor
                              && a.Status != AssignmentStatus.RejectedByManager, ct);

            var attendedCount = await _db.EventAssignments
                .CountAsync(a => a.CrewId == crewUser.Id
                              && !a.IsDeleted
                              && a.Status == AssignmentStatus.Attended, ct);

            // +1 for the one being checked out right now
            attendedCount++;

            if (totalAssignments > 0)
            {
                var attendanceRate = (decimal)attendedCount / totalAssignments * 100m;
                // Weighted blend: 70% existing, 30% latest rate
                var newScore = crewUser.DisciplineScore * 0.7m + attendanceRate * 0.3m;
                crewUser.UpdateDisciplineScore(Math.Round(newScore, 1));
            }
        }

        await _uow.SaveChangesAsync(ct);

        return Result.Success(new AttendanceRecordDto(
            record.Id, record.AssignmentId, record.EventId, record.CrewId,
            assignment.Crew.FullName, action.ToString(), record.RecordedAt, record.Location));
    }
}
