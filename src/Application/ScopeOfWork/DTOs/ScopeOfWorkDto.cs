namespace EventWOS.Application.ScopeOfWork.DTOs;

/// <summary>Read-side shape for the Scope of Work catalog.</summary>
public sealed record ScopeOfWorkDto(
    Guid     Id,
    string   Name,
    string?  Description,
    bool     IsArchived,
    DateTime CreatedAt,
    DateTime? UpdatedAt
);
