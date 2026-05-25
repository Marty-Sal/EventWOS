using EventWOS.Application.Attendance.DTOs;
using EventWOS.Application.Interfaces;
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
    public RecordAttendanceHandler(IAppDbContext db, IUnitOfWork uow) { _db = db; _uow = uow; }

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

        var record = new AttendanceRecord(
            assignment.Id, assignment.EventId, assignment.CrewId,
            action, req.Location, req.RecordedByUserId);

        _db.AttendanceRecords.Add(record);

        // Auto-update assignment status on check-in
        if (action == AttendanceAction.CheckIn && assignment.Status == AssignmentStatus.Confirmed)
            assignment.MarkAttended();

        await _uow.SaveChangesAsync(ct);

        return Result.Success(new AttendanceRecordDto(
            record.Id, record.AssignmentId, record.EventId, record.CrewId,
            assignment.Crew.FullName, action.ToString(), record.RecordedAt, record.Location));
    }
}
