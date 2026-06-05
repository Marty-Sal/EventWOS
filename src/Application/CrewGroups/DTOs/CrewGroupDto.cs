namespace EventWOS.Application.CrewGroups.DTOs;

public sealed record CrewGroupDto(
    Guid     Id,
    Guid     VendorId,
    string   Name,
    string?  Description,
    int      MemberCount,
    DateTime CreatedAt
);

public sealed record CrewGroupMemberDto(
    Guid     Id,
    Guid     CrewId,
    string   FullName,
    string   Mobile,
    decimal  DisciplineScore,
    int      EventsAttended,
    DateTime AddedAt
);

public sealed record CrewGroupDetailDto(
    Guid     Id,
    Guid     VendorId,
    string   Name,
    string?  Description,
    DateTime CreatedAt,
    IReadOnlyList<CrewGroupMemberDto> Members
);

public sealed record VendorAssignGroupResultDto(
    Guid     GroupId,
    string   GroupName,
    int      Invited,
    int      SkippedAlreadyOnEvent,
    int      Failed,
    IReadOnlyList<string> InvitedNames,
    IReadOnlyList<string> SkippedNames,
    IReadOnlyList<VendorAssignGroupFailureDto> Failures
);

public sealed record VendorAssignGroupFailureDto(
    Guid    CrewId,
    string  FullName,
    string  Reason
);
