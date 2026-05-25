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
    Guid      Id,
    Guid      EventId,
    string    EventTitle,
    Guid      CrewId,
    string    CrewName,
    string    CrewMobile,
    decimal   DisciplineScore,
    int       EventsAttended,
    decimal?  CrewRating,        // crew member's overall rolling rating by vendors
    int       CrewRatingCount,
    Guid      VendorId,
    string    VendorName,
    string    Status,
    string?   RejectionReason,
    DateTime? CrewRespondedAt,
    DateTime? VendorReviewedAt,
    DateTime? ManagerReviewedAt,
    DateTime? ConfirmedAt,
    DateTime? DeclinedAt,
    DateTime  CreatedAt,
    decimal?  VendorRating,      // this vendor's rating for this assignment
    DateTime? RatedAt
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
