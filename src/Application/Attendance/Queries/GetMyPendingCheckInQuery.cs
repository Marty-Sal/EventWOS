using EventWOS.Application.Attendance.DTOs;
using EventWOS.Application.Interfaces;
using EventWOS.Domain.Enums;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Attendance.Queries;

/// <summary>
/// Returns the crew's currently-live PendingCheckIn for a given assignment
/// (Status = Pending AND not expired), or a failure Result if none. The
/// modal calls this on first render so a browser refresh doesn't lose a
/// QR that still has time on the clock.
/// </summary>
public sealed record GetMyPendingCheckInQuery(
    Guid AssignmentId,
    Guid CallerUserId
) : IRequest<Result<PendingCheckInDto>>;

public sealed class GetMyPendingCheckInHandler
    : IRequestHandler<GetMyPendingCheckInQuery, Result<PendingCheckInDto>>
{
    private readonly IAppDbContext _db;

    public GetMyPendingCheckInHandler(IAppDbContext db) => _db = db;

    public async Task<Result<PendingCheckInDto>> Handle(
        GetMyPendingCheckInQuery req, CancellationToken ct)
    {
        // Own-data guard first — cheaper than the join.
        var assignmentOwner = await _db.EventAssignments
            .Where(a => a.Id == req.AssignmentId)
            .Select(a => new { a.CrewId, a.EventId, a.Event.Title })
            .FirstOrDefaultAsync(ct);

        if (assignmentOwner is null)
            return Result.Failure<PendingCheckInDto>(new Error(
                "Assignment.NotFound", "Assignment not found."));

        if (assignmentOwner.CrewId != req.CallerUserId)
            return Result.Failure<PendingCheckInDto>(new Error(
                "Assignment.NotYours", "You can only view your own pending check-in."));

        var row = await _db.PendingCheckIns
            .Where(p => p.AssignmentId == req.AssignmentId
                     && p.Status == PendingCheckInStatus.Pending
                     && p.ExpiresAt > DateTime.UtcNow)
            .OrderByDescending(p => p.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (row is null)
            return Result.Failure<PendingCheckInDto>(new Error(
                "CheckIn.NoLiveQR",
                "No live check-in QR — click Check In to generate one."));

        return Result.Success(new PendingCheckInDto(
            Id:           row.Id,
            Code:         row.Code,
            ExpiresAt:    new DateTimeOffset(DateTime.SpecifyKind(row.ExpiresAt, DateTimeKind.Utc), TimeSpan.Zero),
            Status:       row.Status.ToString(),
            AssignmentId: req.AssignmentId,
            EventId:      assignmentOwner.EventId,
            EventTitle:   assignmentOwner.Title));
    }
}
