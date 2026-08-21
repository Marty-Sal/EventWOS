using EventWOS.Domain.Common;

namespace EventWOS.Domain.Entities;

/// <summary>
/// Admin-maintained catalog of physical venues, managed from
/// Settings → Venue. Mirrors the ScopeOfWork catalog pattern exactly
/// (same soft-delete/archive/restore lifecycle, same "unique name among
/// active rows" rule).
///
/// The address is deliberately split into structured fields (rather than
/// one free-text string like Event.Venue/Event.Address today) because
/// this catalog exists specifically to be geocode-able: Latitude/Longitude
/// are nullable placeholders a future geocoding step (manual entry now,
/// an automated lookup later) fills in. Once a venue has coordinates,
/// events held there can inherit them for the 1km attendance-geofencing
/// feature instead of every event needing its own lat/lng entry.
///
/// NOT wired into Event creation yet — this is purely the catalog/CRUD
/// screen. Linking Event → Venue is a follow-up, not part of this change.
/// </summary>
public sealed class Venue : BaseEntity
{
    private Venue() { }

    public Venue(
        string  name,
        string  addressLine1,
        string? addressLine2,
        string  city,
        string? state,
        string? postalCode,
        string? country,
        double? latitude,
        double? longitude,
        string? notes,
        Guid    createdByUserId)
    {
        SetName(name);
        AddressLine1 = NormaliseRequired(addressLine1, nameof(addressLine1), "Address line 1");
        AddressLine2 = Normalise(addressLine2, 200);
        City         = NormaliseRequired(city, nameof(city), "City");
        State        = Normalise(state, 100);
        PostalCode   = Normalise(postalCode, 20);
        Country      = Normalise(country, 100);
        SetCoordinates(latitude, longitude);
        Notes           = Normalise(notes, 1000);
        CreatedByUserId = createdByUserId;
    }

    public string  Name            { get; private set; } = default!;
    public string  AddressLine1    { get; private set; } = default!;
    public string? AddressLine2    { get; private set; }
    public string  City            { get; private set; } = default!;
    public string? State           { get; private set; }
    public string? PostalCode      { get; private set; }
    public string? Country         { get; private set; }
    public double? Latitude        { get; private set; }
    public double? Longitude       { get; private set; }
    public string? Notes           { get; private set; }
    public Guid    CreatedByUserId { get; private set; }

    // ── Behaviours ──────────────────────────────────────────────────────────

    public void Update(
        string name, string addressLine1, string? addressLine2, string city,
        string? state, string? postalCode, string? country,
        double? latitude, double? longitude, string? notes)
    {
        if (IsDeleted)
            throw new InvalidOperationException("Cannot edit an archived venue — restore it first.");

        SetName(name);
        AddressLine1 = NormaliseRequired(addressLine1, nameof(addressLine1), "Address line 1");
        AddressLine2 = Normalise(addressLine2, 200);
        City         = NormaliseRequired(city, nameof(city), "City");
        State        = Normalise(state, 100);
        PostalCode   = Normalise(postalCode, 20);
        Country      = Normalise(country, 100);
        SetCoordinates(latitude, longitude);
        Notes        = Normalise(notes, 1000);
        UpdatedAt    = DateTime.UtcNow;
    }

    /// <summary>Soft-delete. Idempotent.</summary>
    public void Archive(Guid actingUserId)
    {
        if (IsDeleted) return;
        IsDeleted = true;
        DeletedAt = DateTime.UtcNow;
        DeletedBy = actingUserId;
    }

    /// <summary>Un-archive. Idempotent.</summary>
    public void Restore()
    {
        if (!IsDeleted) return;
        IsDeleted = false;
        DeletedAt = null;
        DeletedBy = null;
        UpdatedAt = DateTime.UtcNow;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private void SetName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Venue name is required.", nameof(name));
        var trimmed = name.Trim();
        if (trimmed.Length > 120)
            throw new ArgumentException("Venue name must be 120 characters or fewer.", nameof(name));
        Name = trimmed;
    }

    private void SetCoordinates(double? latitude, double? longitude)
    {
        if (latitude is < -90 or > 90)
            throw new ArgumentException("Latitude must be between -90 and 90.", nameof(latitude));
        if (longitude is < -180 or > 180)
            throw new ArgumentException("Longitude must be between -180 and 180.", nameof(longitude));
        Latitude  = latitude;
        Longitude = longitude;
    }

    private static string NormaliseRequired(string value, string paramName, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{label} is required.", paramName);
        var trimmed = value.Trim();
        if (trimmed.Length > 200)
            throw new ArgumentException($"{label} must be 200 characters or fewer.", paramName);
        return trimmed;
    }

    private static string? Normalise(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
            throw new ArgumentException($"Value must be {maxLength} characters or fewer.");
        return trimmed;
    }
}
