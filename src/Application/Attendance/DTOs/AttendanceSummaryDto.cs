namespace EventWOS.Application.Attendance.DTOs;

public sealed record AttendanceSummaryDto(
    Guid   EventId,
    string EventTitle,
    int    TotalAssigned,
    int    TotalConfirmed,
    int    TotalAttended,
    int    TotalNoShow,
    IReadOnlyList<CrewAttendanceDto> CrewDetails
);

public sealed record CrewAttendanceDto(
    Guid     CrewId,
    string   CrewName,
    string   AssignmentStatus,
    DateTime? CheckInAt,
    DateTime? CheckOutAt
);
