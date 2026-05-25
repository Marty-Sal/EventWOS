using EventWOS.Application.Attendance.DTOs;
using EventWOS.Application.Interfaces;
using EventWOS.Domain.Enums;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Events.Queries;

public sealed record GetAttendanceSummaryQuery(Guid EventId) : IRequest<Result<AttendanceSummaryDto>>;

public sealed class GetAttendanceSummaryHandler : IRequestHandler<GetAttendanceSummaryQuery, Result<AttendanceSummaryDto>>
{
    private readonly IAppDbContext _db;
    public GetAttendanceSummaryHandler(IAppDbContext db) => _db = db;

    public async Task<Result<AttendanceSummaryDto>> Handle(GetAttendanceSummaryQuery req, CancellationToken ct)
    {
        var ev = await _db.Events.FindAsync(new object[] { req.EventId }, ct);
        if (ev is null) return Result.Failure<AttendanceSummaryDto>(new Error("Event.NotFound", "Event not found."));

        var assignments = await _db.EventAssignments
            .Include(a => a.Crew)
            .Where(a => a.EventId == req.EventId)
            .ToListAsync(ct);

        var assignmentIds = assignments.Select(a => a.Id).ToList();

        var records = await _db.AttendanceRecords
            .Where(r => assignmentIds.Contains(r.AssignmentId))
            .OrderBy(r => r.RecordedAt)
            .ToListAsync(ct);

        var recordsByAssignment = records.GroupBy(r => r.AssignmentId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var crewDetails = assignments.Select(a =>
        {
            var aRecords = recordsByAssignment.GetValueOrDefault(a.Id, new());
            var checkIn  = aRecords.FirstOrDefault(r => r.Action == AttendanceAction.CheckIn)?.RecordedAt;
            var checkOut = aRecords.LastOrDefault (r => r.Action == AttendanceAction.CheckOut)?.RecordedAt;
            return new CrewAttendanceDto(a.CrewId, a.Crew.FullName, a.Status.ToString(), checkIn, checkOut);
        }).ToList();

        return Result.Success(new AttendanceSummaryDto(
            ev.Id, ev.Title,
            assignments.Count,
            assignments.Count(a => a.Status == AssignmentStatus.Confirmed || a.Status == AssignmentStatus.Attended),
            assignments.Count(a => a.Status == AssignmentStatus.Attended),
            assignments.Count(a => a.Status == AssignmentStatus.NoShow),
            crewDetails));
    }
}
