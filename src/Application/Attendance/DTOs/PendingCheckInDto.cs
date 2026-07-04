namespace EventWOS.Application.Attendance.DTOs;

/// <summary>
/// What the crew's phone gets back when they click "Check In".
/// Contains only the two things the QR modal needs to render:
///   Code       → the string to encode in the QR
///   ExpiresAt  → drives the 10-min countdown timer
/// </summary>
public sealed record PendingCheckInDto(
    Guid     Id,
    string   Code,
    DateTimeOffset ExpiresAt,
    string   Status,
    Guid     AssignmentId,
    Guid     EventId,
    string   EventTitle);

/// <summary>
/// What the vendor's scan page gets back after a successful verify. The
/// AssignmentId + CrewName let the vendor immediately show a toast like
/// "Checked in: Sam Martin — Box Office" without a follow-up fetch.
/// </summary>
public sealed record CheckInVerifyResultDto(
    Guid   AssignmentId,
    Guid   CrewId,
    string CrewName,
    Guid   EventId,
    string EventTitle,
    string? ShiftScopeName,
    DateTimeOffset CheckedInAt);
