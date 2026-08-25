namespace EventOpsOracle.Domain.Enums;

/// <summary>
/// Which side of an event a <see cref="Entities.Rating"/> is about.
///
/// Stored explicitly rather than inferred from the subject's
/// <see cref="UserRole"/>, because a role is mutable: promoting a Crew member
/// to Vendor must not silently re-file every rating they earned as Crew.
/// The rating records what the person was being judged as AT THE TIME.
/// </summary>
public enum RatingSubjectType
{
    /// <summary>Admin/Manager rating a Vendor when the event is marked complete.</summary>
    Vendor = 1,

    /// <summary>Vendor rating one of their Crew when that crew member checks out.</summary>
    Crew   = 2
}
