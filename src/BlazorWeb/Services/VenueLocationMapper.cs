namespace EventWOS.BlazorWeb.Services;

/// <summary>
/// The venue form's location fields, as a value. Deliberately a record rather
/// than loose parameters so a caller cannot silently forget one — adding a field
/// here breaks every call site at compile time.
/// </summary>
public sealed record VenueLocationFields(
    string? Name,
    string? AddressLine1,
    string? ShortAddress,
    string? City,
    string? State,
    string? PostalCode,
    string? Country,
    double? Latitude,
    double? Longitude);

/// <summary>
/// Pure mapping from a provider result onto the venue form.
///
/// This lives in its own dependency-free class for one reason: it was originally
/// inline in Venue.razor, and a later edit to the address-filling block silently
/// deleted the two lines that set the coordinates. Picking a place then filled
/// the address perfectly while leaving the map at its fallback view. Inline UI
/// code with no test could not catch that; this class is unit-tested and every
/// rule below is asserted.
///
/// Two rules govern everything here:
///
///  1. COORDINATES ALWAYS WIN. They are the point of picking a suggestion and
///     they drive the geofence. Never conditional.
///  2. ADDRESS TEXT FILLS BLANKS ONLY. Geocoders are routinely wrong about unit
///     numbers and hall names, so hand-typed text is never overwritten.
/// </summary>
public static class VenueLocationMapper
{
    public const int NameMaxLength         = 120;
    public const int AddressLineMaxLength  = 200;
    public const int ShortAddressMaxLength = 200;
    public const int CityMaxLength         = 200;
    public const int PostalCodeMaxLength   = 20;
    public const int CountryMaxLength      = 100;

    /// <summary>
    /// Apply a chosen search suggestion.
    /// <paramref name="canonicalStates"/> is the exact option list rendered by
    /// StateSelect; a state that cannot be matched to one comes back null.
    /// </summary>
    public static VenueLocationFields ApplySuggestion(
        VenueLocationFields current,
        LocationSuggestion suggestion,
        IReadOnlyList<string> canonicalStates)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(suggestion);

        return current with
        {
            // Rule 1 — unconditional.
            Latitude  = (double)suggestion.Latitude,
            Longitude = (double)suggestion.Longitude,

            // ShortAddress is the exception among the text fields: it exists so
            // the admin never has to compose it, so the provider's version always
            // replaces whatever is there.
            ShortAddress = Truncate(suggestion.ShortAddress, ShortAddressMaxLength),

            // Rule 2 — blanks only.
            Name = FillBlank(current.Name, suggestion.Name, NameMaxLength),
            AddressLine1 = FillBlank(
                current.AddressLine1,
                string.IsNullOrWhiteSpace(suggestion.FullAddress) ? suggestion.Name : suggestion.FullAddress,
                AddressLineMaxLength),
            City       = FillBlank(current.City,       suggestion.City,       CityMaxLength),
            PostalCode = FillBlank(current.PostalCode, suggestion.PostalCode, PostalCodeMaxLength),
            Country    = FillBlank(current.Country,    suggestion.Country,    CountryMaxLength),
            State      = string.IsNullOrWhiteSpace(current.State)
                ? MatchCanonicalState(suggestion.State, canonicalStates) ?? current.State
                : current.State,
        };
    }

    /// <summary>
    /// Apply a reverse-geocode result after the admin drags the pin.
    /// The caller has already moved the marker, so <paramref name="latitude"/> /
    /// <paramref name="longitude"/> are passed explicitly and still win — the
    /// dragged point is authoritative even if the provider returns no address.
    /// </summary>
    public static VenueLocationFields ApplyReverseGeocode(
        VenueLocationFields current,
        LocationDetail? detail,
        double latitude,
        double longitude,
        IReadOnlyList<string> canonicalStates)
    {
        ArgumentNullException.ThrowIfNull(current);

        var moved = current with { Latitude = latitude, Longitude = longitude };

        // No address at that point (middle of a field, provider down) is a
        // survivable outcome: keep the coordinates, skip the labels.
        if (detail is null) return moved;

        return moved with
        {
            AddressLine1 = FillBlank(moved.AddressLine1, detail.Address,    AddressLineMaxLength),
            City         = FillBlank(moved.City,         detail.City,       CityMaxLength),
            PostalCode   = FillBlank(moved.PostalCode,   detail.PostalCode, PostalCodeMaxLength),
            Country      = FillBlank(moved.Country,      detail.Country,    CountryMaxLength),
            State        = string.IsNullOrWhiteSpace(moved.State)
                ? MatchCanonicalState(detail.State, canonicalStates) ?? moved.State
                : moved.State,
        };
    }

    /// <summary>
    /// Map a provider state name onto the exact option text StateSelect renders.
    ///
    /// StateSelect is a plain select: a value that is not an exact option match
    /// renders as "Select state…", so an unmatched string would look like nothing
    /// happened while quietly sitting in the form. Returning null in that case is
    /// deliberate — an empty dropdown the admin must fill is honest, a
    /// silently-wrong one is not.
    /// </summary>
    public static string? MatchCanonicalState(string? providerState, IReadOnlyList<string> canonicalStates)
    {
        if (string.IsNullOrWhiteSpace(providerState) || canonicalStates is null || canonicalStates.Count == 0)
            return null;

        var raw = providerState.Trim();

        // 1. Exact (case-insensitive) match — the common path.
        var exact = canonicalStates.FirstOrDefault(x => string.Equals(x, raw, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        // 2. Renames and long forms OSM still carries.
        var alias = raw.ToLowerInvariant() switch
        {
            "orissa"                              => "Odisha",
            "pondicherry"                         => "Puducherry",
            "uttaranchal"                         => "Uttarakhand",
            "nct of delhi"                        => "Delhi",
            "national capital territory of delhi" => "Delhi",
            _                                     => null,
        };
        if (alias is not null)
        {
            var mapped = canonicalStates.FirstOrDefault(x => string.Equals(x, alias, StringComparison.OrdinalIgnoreCase));
            if (mapped is not null) return mapped;
        }

        // 3. A single unambiguous containment match ("Maharashtra State").
        //    Ambiguity means give up rather than guess.
        var contains = canonicalStates
            .Where(x => raw.Contains(x, StringComparison.OrdinalIgnoreCase)
                     || x.Contains(raw, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return contains.Count == 1 ? contains[0] : null;
    }

    private static string? FillBlank(string? currentValue, string? incoming, int maxLength)
        => !string.IsNullOrWhiteSpace(currentValue) || string.IsNullOrWhiteSpace(incoming)
            ? currentValue
            : Truncate(incoming!, maxLength);

    /// <summary>
    /// Provider strings routinely exceed our column widths (display_name is often
    /// 150+ chars). Trim here so a save fails on real validation problems, not on
    /// a length the admin never chose.
    /// </summary>
    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];
}
