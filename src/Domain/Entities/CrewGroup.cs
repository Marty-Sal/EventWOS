using EventWOS.Domain.Common;

namespace EventWOS.Domain.Entities;

/// <summary>
/// A vendor-scoped, named bucket of crew members so a vendor can
/// invite a whole team to an event in one click (e.g. "Lighting A-Team",
/// "Mumbai Pool"). A crew member may belong to multiple groups.
/// Groups never replace per-assignment lifecycle — inviting a group
/// just fans out to one EventAssignment per member.
/// </summary>
public sealed class CrewGroup : BaseEntity
{
    private CrewGroup() { }

    public CrewGroup(Guid vendorId, string name, string? description, Guid createdByUserId)
    {
        if (vendorId == Guid.Empty) throw new ArgumentException("VendorId required.", nameof(vendorId));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Group name required.", nameof(name));

        VendorId    = vendorId;
        Name        = name.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        CreatedBy   = createdByUserId;
    }

    public Guid    VendorId    { get; private set; }
    public string  Name        { get; private set; } = default!;
    public string? Description { get; private set; }

    // Navigation
    public User Vendor { get; private set; } = default!;
    public ICollection<CrewGroupMember> Members { get; private set; } = new List<CrewGroupMember>();

    public void Rename(string name, Guid updatedByUserId)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Group name required.", nameof(name));
        Name      = name.Trim();
        UpdatedAt = DateTime.UtcNow;
        UpdatedBy = updatedByUserId;
    }

    public void SetDescription(string? description, Guid updatedByUserId)
    {
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        UpdatedAt   = DateTime.UtcNow;
        UpdatedBy   = updatedByUserId;
    }

    public void SoftDelete(Guid deletedByUserId)
    {
        IsDeleted = true;
        DeletedAt = DateTime.UtcNow;
        DeletedBy = deletedByUserId;
    }
}
