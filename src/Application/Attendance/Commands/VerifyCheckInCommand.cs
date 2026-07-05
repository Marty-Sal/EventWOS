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

/// <summary>
/// Vendor (or Manager/Admin fallback) scans the crew's QR → the scanned
/// code is POSTed to /api/v1/attendance/checkin/verify. We validate the
/// row is still Pending + not expired, confirm the caller is authorised to
/// verify THIS assignment (owns the vendor slot, or is a Manager/Admin),
/// and then commit an actual AttendanceRecord (CheckIn) + flip assignment
/// status to Attended.
///
/// This handler intentionally duplicates a subset of RecordAttendanceHandler's
/// write logic rather than delegating to it — we need the writes to happen
/// in the same SaveChangesAsync as the PendingCheckIn.MarkConsumed() so a
/// mid-transaction failure never leaves us with a Consumed row but no
/// AttendanceRecord (or vice-versa).
/// </summary>
public sealed record VerifyCheckInCommand(
    string Code,
    Guid   VerifierUserId,
    UserRole VerifierRole,
    string? Location
) : IRequest<Result<CheckInVerifyResultDto>>;

public sealed class VerifyCheckInHandler
    : IRequestHandler<VerifyCheckInCommand, Result<CheckInVerifyResultDto>>
{
    private readonly IAppDbContext _db;
    private readonly IUnitOfWork   _uow;
    private readonly INotificationPusher _push;
    private readonly IGeoLocationService _geo;

    public VerifyCheckInHandler(
        IAppDbContext db, IUnitOfWork uow, INotificationPusher push,
        IGeoLocationService geo)
    {
        _db   = db;
        _uow  = uow;
        _push = push;
        _geo  = geo;
    }

    public async Task<Result<CheckInVerifyResultDto>> Handle(
        VerifyCheckInCommand req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Code))
            return Result.Failure<CheckInVerifyResultDto>(new Error(
                "CheckIn.CodeMissing", "No code provided."));

        // Normalise: strip whitespace + uppercase. Our alphabet is uppercase
        // by construction; being lenient on input avoids UX pain when a
        // vendor manually types the fallback code and hits caps-lock.
        var code = req.Code.Trim().Replace(" ", "").ToUpperInvariant();

        // ── Lookup ─────────────────────────────────────────────────────
        // We eagerly include the assignment (+ its Crew/Event/Shift) because
        // we need those to build the success DTO and to validate the
        // vendor-ownership rule.
        var pending = await _db.PendingCheckIns
            .FirstOrDefaultAsync(p => p.Code == code, ct);

        if (pending is null)
            return Result.Failure<CheckInVerifyResultDto>(new Error(
                "CheckIn.CodeNotFound",
                "This code is not recognised. Ask the crew to regenerate the QR."));

        if (pending.Status == PendingCheckInStatus.Consumed)
            return Result.Failure<CheckInVerifyResultDto>(new Error(
                "CheckIn.AlreadyVerified",
                "This QR has already been used."));

        if (pending.Status == PendingCheckInStatus.Cancelled)
            return Result.Failure<CheckInVerifyResultDto>(new Error(
                "CheckIn.Cancelled",
                "This QR was superseded by a newer one. Ask the crew for the latest QR."));

        if (pending.IsExpired || pending.Status == PendingCheckInStatus.Expired)
        {
            // Lazy sweep — flip the row so future reads see the right status.
            pending.MarkExpired();
            await _uow.SaveChangesAsync(ct);
            return Result.Failure<CheckInVerifyResultDto>(new Error(
                "CheckIn.Expired",
                "This QR has expired. Ask the crew to regenerate it."));
        }

        // ── Load the assignment + related nav data ─────────────────────
        var assignment = await _db.EventAssignments
            .Include(a => a.Event)
            .Include(a => a.Crew)
            .FirstOrDefaultAsync(a => a.Id == pending.AssignmentId, ct);

        if (assignment is null || assignment.Crew is null)
            return Result.Failure<CheckInVerifyResultDto>(new Error(
                "Assignment.NotFound",
                "The assignment linked to this QR no longer exists."));

        // ── Authorisation ──────────────────────────────────────────────
        // Vendor must own the assignment's VendorId slot; Manager + Admin
        // are always allowed (break-glass / no-vendor-on-site fallback).
        var isAuthorisedVerifier = req.VerifierRole switch
        {
            UserRole.Admin   => true,
            UserRole.Manager => true,
            UserRole.Vendor  => assignment.VendorId == req.VerifierUserId,
            _                => false
        };

        if (!isAuthorisedVerifier)
            return Result.Failure<CheckInVerifyResultDto>(new Error(
                "CheckIn.NotAuthorised",
                "You are not authorised to verify this crew's check-in."));

        // ── Belt-and-braces: has the crew already checked in via some other path? ──
        // (E.g. a legacy client that bypassed the QR flow — shouldn't happen
        // once the crew UI is updated, but we keep the guard so audit stays clean.)
        var alreadyCheckedIn = await _db.AttendanceRecords
            .AnyAsync(r => r.AssignmentId == assignment.Id
                        && r.Action == AttendanceAction.CheckIn, ct);
        if (alreadyCheckedIn)
        {
            // The QR is still valid but redundant — flip it to Consumed so
            // it doesn't dangle, but return an "already checked in" hint.
            pending.MarkConsumed(req.VerifierUserId);
            await _uow.SaveChangesAsync(ct);
            return Result.Failure<CheckInVerifyResultDto>(new Error(
                "Attendance.AlreadyCheckedIn",
                "This crew is already checked in."));
        }

        // ── Commit: write the AttendanceRecord + flip assignment status ─
        var attendance = new AttendanceRecord(
            assignmentId:     assignment.Id,
            eventId:          assignment.EventId,
            crewId:           assignment.Crew.Id,
            action:           AttendanceAction.CheckIn,
            // Enrich "lat,lng" → "lat,lng|City, State, Country" via the
            // embedded GeoNames cities15000 dataset. Idempotent: rows
            // already carrying a "|address" tail pass through untouched;
            // null / "unavailable:*" / unparseable inputs pass through too.
            location:         _geo.Enrich(req.Location),
            recordedByUserId: req.VerifierUserId.ToString());

        _db.AttendanceRecords.Add(attendance);

        if (assignment.IsEligibleForAttendance)
            assignment.MarkAttended();

        pending.MarkConsumed(req.VerifierUserId);

        await _uow.SaveChangesAsync(ct);

        // ── Push realtime notification to the crew ─────────────────────
        // Their QR modal is listening for "CheckInVerified" and will
        // close itself + refresh the page.
        _ = _push.PushToUserAsync(assignment.Crew.Id, "CheckInVerified", new
        {
            assignmentId = assignment.Id,
            eventId      = assignment.EventId,
            eventTitle   = assignment.Event.Title,
            checkedInAt  = attendance.RecordedAt
        }, ct);

        // Resolve shift name for the DTO (nullable — legacy assignments).
        string? shiftScopeName = null;
        if (assignment.ShiftId.HasValue)
        {
            shiftScopeName = await _db.EventShifts
                .Where(s => s.Id == assignment.ShiftId.Value)
                .Select(s => (string?)s.ScopeOfWork.Name)
                .FirstOrDefaultAsync(ct);
        }

        return Result.Success(new CheckInVerifyResultDto(
            AssignmentId:   assignment.Id,
            CrewId:         assignment.Crew.Id,
            CrewName:       assignment.Crew.FullName,
            EventId:        assignment.EventId,
            EventTitle:     assignment.Event.Title,
            ShiftScopeName: shiftScopeName,
            CheckedInAt:    new DateTimeOffset(DateTime.SpecifyKind(attendance.RecordedAt, DateTimeKind.Utc), TimeSpan.Zero)));
    }
}
