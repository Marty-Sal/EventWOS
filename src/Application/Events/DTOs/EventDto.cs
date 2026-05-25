namespace EventWOS.Application.Events.DTOs;

public sealed record EventDto(
    Guid   Id,
    string Title,
    string? Description,
    string Venue,
    string? Address,
    DateTime StartAt,
    DateTime EndAt,
    string Status,
    int    MaxCrew,
    int    AssignedCrew,
    Guid   CreatedByUserId,
    string CreatedByName,
    DateTime CreatedAt
);

public sealed record EventListItemDto(
    Guid   Id,
    string Title,
    string Venue,
    DateTime StartAt,
    DateTime EndAt,
    string Status,
    int    MaxCrew,
    int    AssignedCrew,
    DateTime CreatedAt
);

public sealed record EventAssignmentDto(
    Guid   Id,
    Guid   EventId,
    string EventTitle,
    Guid   CrewId,
    string CrewName,
    string CrewMobile,
    Guid   VendorId,
    string VendorName,
    string Status,
    DateTime? ConfirmedAt,
    DateTime? DeclinedAt,
    DateTime CreatedAt
);

public sealed record AttendanceRecordDto(
    Guid     Id,
    Guid     AssignmentId,
    Guid     EventId,
    Guid     CrewId,
    string   CrewName,
    string   Action,
    DateTime RecordedAt,
    string?  Location
);

public sealed record PagedEventResult(
    IReadOnlyList<EventListItemDto> Items,
    int TotalCount, int Page, int PageSize);

public sealed record PagedAssignmentResult(
    IReadOnlyList<EventAssignmentDto> Items,
    int TotalCount, int Page, int PageSize);
