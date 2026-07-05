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
    DateTime CreatedAt,
    // Phase D step 21: "approved & ready to work" subset of AssignedCrew.
    // Includes only ManagerApproved / Confirmed / Attended statuses;
    // excludes Invited / VendorApproved / PendingManagerApproval.
    // Used by admin Events card to show real fulfillment.
    int    ConfirmedCrew = 0
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
    DateTime CreatedAt,
    // Phase D step 21: see EventDto.ConfirmedCrew. Optional with default
    // 0 so older code paths still compile (e.g. CreateEventCommand return).
    int    ConfirmedCrew = 0
);

public sealed record EventAssignmentDto(
    Guid      Id,
    Guid      EventId,
    string    EventTitle,
    string    EventStatus,
    Guid?     CrewId,
    string?   CrewName,
    string?   CrewMobile,
    decimal   DisciplineScore,
    int       EventsAttended,
    decimal?  CrewRating,        // crew member's overall rolling rating by vendors
    int       CrewRatingCount,
    Guid?     VendorId,
    string?   VendorName,
    string    Status,
    string?   RejectionReason,
    DateTime? CrewRespondedAt,
    DateTime? VendorReviewedAt,
    DateTime? ManagerReviewedAt,
    DateTime? ConfirmedAt,
    DateTime? DeclinedAt,
    DateTime  CreatedAt,
    decimal?  VendorRating,      // this vendor's rating for this assignment
    DateTime? RatedAt,
    string?   AttendanceNote,      // admin override note, if any (e.g. "Marked attended by Admin Saly on 2026-06-06")
    Guid?     ShiftId,             // which EventShift this assignment fills (nullable for legacy rows from before multi-shift)
    string?   ShiftScopeName,      // denormalised "Box Office" / "F&B" label for UI grouping; null when ShiftId is null
    DateTime? ShiftStartAt,        // shift start time (UTC); null when ShiftId is null
    DateTime? ShiftEndAt           // shift end time (UTC); null when ShiftId is null OR shift has no defined end
);

public sealed record AttendanceRecordDto(
    Guid     Id,
    Guid     AssignmentId,
    Guid     EventId,
    Guid     CrewId,
    string   CrewName,
    string   Action,
    DateTime RecordedAt,
    string?  LocationAddress,
    string?  LocationCoords
);

public sealed record PagedEventResult(
    IReadOnlyList<EventListItemDto> Items,
    int TotalCount, int Page, int PageSize);

public sealed record PagedAssignmentResult(
    IReadOnlyList<EventAssignmentDto> Items,
    int TotalCount, int Page, int PageSize);


/// <summary>
/// Phase B: shape of one staffing slot on an event. Returned by
/// GetEventShiftsQuery; consumed by the create-event UI (read-back on edit)
/// and by Phase D's crew portal hours display.
/// </summary>
public sealed record EventShiftDto(
    Guid     Id,
    Guid     EventId,
    Guid     ScopeOfWorkId,
    string   ScopeName,
    int      CrewCount,
    int      AssignedCrew,     // current OccupiesSeat count on this shift
    DateTime StartAt,
    DateTime? EndAt
);
