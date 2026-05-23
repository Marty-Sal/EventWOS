using EventWOS.Domain.Common;

namespace EventWOS.Domain.Entities;

/// <summary>
/// Maps a Vendor to their Crew members. Supports approval workflow
/// and metadata about the vendor-crew relationship.
/// </summary>
public sealed class VendorCrewMapping : BaseEntity
{
    private VendorCrewMapping() { }

    public VendorCrewMapping(Guid vendorId, Guid crewId, Guid approvedByManagerId)
    {
        VendorId = vendorId;
        CrewId = crewId;
        ApprovedByManagerId = approvedByManagerId;
        IsActive = true;
        MappedAt = DateTime.UtcNow;
    }

    public Guid VendorId { get; private set; }
    public Guid CrewId { get; private set; }
    public Guid ApprovedByManagerId { get; private set; }
    public bool IsActive { get; private set; }
    public DateTime MappedAt { get; private set; }
    public DateTime? RemovedAt { get; private set; }
    public string? Notes { get; private set; }

    // Navigation
    public User Vendor { get; private set; } = default!;
    public User Crew { get; private set; } = default!;
    public User ApprovedBy { get; private set; } = default!;

    public void Deactivate()
    {
        IsActive = false;
        RemovedAt = DateTime.UtcNow;
    }

    public void SetNotes(string notes) => Notes = notes;
}
