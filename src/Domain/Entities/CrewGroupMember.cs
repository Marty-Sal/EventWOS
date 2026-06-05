using EventWOS.Domain.Common;

namespace EventWOS.Domain.Entities;

/// <summary>
/// Membership join row: which crew belong to which group. Soft-deletable so
/// removing a member preserves audit history. Uniqueness on (GroupId, CrewId)
/// is enforced by a filtered unique index in Persistence.
/// </summary>
public sealed class CrewGroupMember : BaseEntity
{
    private CrewGroupMember() { }

    public CrewGroupMember(Guid crewGroupId, Guid crewId, Guid addedByUserId)
    {
        if (crewGroupId == Guid.Empty) throw new ArgumentException("CrewGroupId required.", nameof(crewGroupId));
        if (crewId      == Guid.Empty) throw new ArgumentException("CrewId required.",      nameof(crewId));

        CrewGroupId = crewGroupId;
        CrewId      = crewId;
        AddedAt     = DateTime.UtcNow;
        CreatedBy   = addedByUserId;
    }

    public Guid     CrewGroupId { get; private set; }
    public Guid     CrewId      { get; private set; }
    public DateTime AddedAt     { get; private set; }

    // Navigation
    public CrewGroup CrewGroup { get; private set; } = default!;
    public User      Crew      { get; private set; } = default!;

    public void SoftDelete(Guid removedByUserId)
    {
        IsDeleted = true;
        DeletedAt = DateTime.UtcNow;
        DeletedBy = removedByUserId;
    }
}
