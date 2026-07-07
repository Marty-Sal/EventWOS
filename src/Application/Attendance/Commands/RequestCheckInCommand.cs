using EventWOS.Application.Attendance.DTOs;
using EventWOS.Application.Interfaces;
using EventWOS.Domain.Entities;
using EventWOS.Domain.Enums;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;

namespace EventWOS.Application.Attendance.Commands;

/// <summary>
/// Crew clicks "Check In" → mint a PendingCheckIn row and hand back the
/// code + expiry. Called by /api/v1/attendance/checkin/request.
/// If a prior Pending row exists for this assignment, cancel it first —
/// only one live QR per assignment at any moment.
/// </summary>
public sealed record RequestCheckInCommand(
    Guid AssignmentId,
    Guid CallerUserId,
    string? CrewLocation
) : IRequest<Result<PendingCheckInDto>>;

public sealed class RequestCheckInHandler
    : IRequestHandler<RequestCheckInCommand, Result<PendingCheckInDto>>
{
    private readonly IAppDbContext _db;
    private readonly IUnitOfWork   _uow;

    // 10-minute TTL — matches the OtpRequest convention used elsewhere in the
    // project, and long enough that a vendor walking from gate → box-office
    // still has time to scan without pressure.
    private const int TtlMinutes = 10;

    public RequestCheckInHandler(IAppDbContext db, IUnitOfWork uow)
    {
        _db  = db;
        _uow = uow;
    }

    public async Task<Result<PendingCheckInDto>> Handle(
        RequestCheckInCommand req, CancellationToken ct)
    {
        // ── Guard 1: assignment exists and the caller owns it ───────────
        var assignment = await _db.EventAssignments
            .Include(a => a.Event)
            .FirstOrDefaultAsync(a => a.Id == req.AssignmentId, ct);

        if (assignment is null)
            return Result.Failure<PendingCheckInDto>(new Error(
                "Assignment.NotFound", "Assignment not found."));

        if (assignment.CrewId != req.CallerUserId)
            return Result.Failure<PendingCheckInDto>(new Error(
                "Assignment.NotYours",
                "You can only request check-in for your own assignment."));

        // ── Guard 2: event must be InProgress ──────────────────────────
        // Same guard as RecordAttendanceHandler — prevents crew generating
        // pre-event QRs.
        if (assignment.Event.Status != EventStatus.InProgress)
            return Result.Failure<PendingCheckInDto>(new Error(
                "Event.NotInProgress",
                "Check-in QR can only be generated while the event is in progress."));

        // ── Guard 3: not already checked in ────────────────────────────
        // If crew is already checked in, we don't want them minting more
        // QRs — surface the state instead.
        var alreadyCheckedIn = await _db.AttendanceRecords
            .AnyAsync(r => r.AssignmentId == assignment.Id
                        && r.Action == AttendanceAction.CheckIn, ct);
        if (alreadyCheckedIn)
            return Result.Failure<PendingCheckInDto>(new Error(
                "Attendance.AlreadyCheckedIn",
                "You have already checked in for this event."));

        // ── Cancel any prior Pending row for this assignment ───────────
        // "Regenerate QR" semantics: minting a fresh row supersedes the old
        // one. We flip it to Cancelled (not Expired) so audit tells us the
        // crew chose to refresh vs. let it lapse.
        var priors = await _db.PendingCheckIns
            .Where(p => p.AssignmentId == assignment.Id
                     && p.Status == PendingCheckInStatus.Pending)
            .ToListAsync(ct);
        foreach (var prior in priors)
            prior.MarkCancelled();

        // ── Guard 4: crew location must be present ─────────────────────
        // Product contract: attendance records must carry the crew's coords
        // at the moment of Check In (not the vendor's scanning device).
        // The client's LocationRequiredModal prevents the crew from getting
        // this far without a fix, but we validate defensively so a rogue
        // API call can't bypass the policy. The location shape is a lax
        // "lat,lng" match — precision is set by checkin.js (6 dp) but we
        // don't enforce that here; a future device rounding tweak
        // shouldn't trigger a server-side reject.
        var loc = (req.CrewLocation ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(loc)
            || !System.Text.RegularExpressions.Regex.IsMatch(
                loc, @"^-?\d{1,3}\.\d+,-?\d{1,3}\.\d+$"))
        {
            return Result.Failure<PendingCheckInDto>(new Error(
                "CheckIn.LocationRequired",
                "Location is required to check in. Please enable location access and try again."));
        }

        // ── Mint the new row ───────────────────────────────────────────
        var code = GenerateCode();
        var row  = new PendingCheckIn(
            assignmentId: assignment.Id,
            crewId:       assignment.CrewId!.Value,
            eventId:      assignment.EventId,
            shiftId:      assignment.ShiftId,
            code:         code,
            crewLocation: loc,
            ttlMinutes:   TtlMinutes);

        _db.PendingCheckIns.Add(row);
        await _uow.SaveChangesAsync(ct);

        return Result.Success(new PendingCheckInDto(
            Id:           row.Id,
            Code:         row.Code,
            ExpiresAt:    new DateTimeOffset(DateTime.SpecifyKind(row.ExpiresAt, DateTimeKind.Utc), TimeSpan.Zero),
            Status:       row.Status.ToString(),
            AssignmentId: assignment.Id,
            EventId:      assignment.EventId,
            EventTitle:   assignment.Event.Title));
    }

    /// <summary>
    /// Cryptographically-random 12-char base32 code (60 bits entropy). Uses
    /// RandomNumberGenerator so it's safe against prediction; base32 chosen
    /// over hex/base64 because the alphabet is ambiguity-free
    /// (no 0/O/1/I/l) — matters if a vendor ever has to type it in as fallback.
    /// </summary>
    private static string GenerateCode()
    {
        const string alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789"; // 30 chars, no 0/O/1/I/L
        Span<byte> bytes = stackalloc byte[12];
        RandomNumberGenerator.Fill(bytes);
        Span<char> chars = stackalloc char[12];
        for (var i = 0; i < 12; i++)
            chars[i] = alphabet[bytes[i] % alphabet.Length];
        return new string(chars);
    }
}
