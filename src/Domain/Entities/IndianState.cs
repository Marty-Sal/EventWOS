using EventWOS.Domain.Common;

namespace EventWOS.Domain.Entities;

/// <summary>
/// Static reference data: the 28 Indian states + 8 union territories.
/// Seeded once by DatabaseSeeder and never mutated at runtime — this is
/// the single source of truth every "State" field in the app should read
/// from (Venue catalog, Vendor/Crew registration, profile editing), via a
/// dropdown, instead of free text. Free-text state entry was producing
/// "Maharashtra" / "maharashtra" / "MH" drift with no way to reliably
/// group or filter by state.
/// </summary>
public sealed class IndianState : BaseEntity
{
    private IndianState() { }

    public IndianState(string name, bool isUnionTerritory, int sortOrder)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("State name is required.", nameof(name));
        Name             = name.Trim();
        IsUnionTerritory = isUnionTerritory;
        SortOrder        = sortOrder;
    }

    public string Name             { get; private set; } = default!;
    public bool   IsUnionTerritory { get; private set; }

    /// <summary>Display order for the dropdown — states first (alphabetical), then union territories (alphabetical), matching how most Indian government forms list them.</summary>
    public int    SortOrder        { get; private set; }
}
