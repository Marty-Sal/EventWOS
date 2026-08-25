using EventOpsOracle.Domain.Enums;

namespace EventOpsOracle.Application.Terms.DTOs;

/// <summary>One version of a Terms & Conditions document.</summary>
public sealed record TermsDto(
    Guid          Id,
    TermsAudience Audience,
    int           Version,
    string        Content,
    DateTime      CreatedAt,
    Guid?         CreatedBy
);

/// <summary>
/// Shape returned to a logged-in user to decide whether to show the
/// mandatory re-accept modal. If RequiresAcceptance is false, either no
/// T&amp;C exists yet for the user's role, or they've already accepted the
/// current version — nothing to show.
/// </summary>
public sealed record TermsStatusDto(
    bool           RequiresAcceptance,
    TermsAudience? Audience,
    int?           CurrentVersion,
    string?        Content
);
