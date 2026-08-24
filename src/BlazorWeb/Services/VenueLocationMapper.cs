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
///  2. ADDRESS TEXT FOLLOWS THE PIN. Geocoders are routinely wrong about unit
///     numbers and hall names, so a REFINEMENT (nudging the pin, or re-picking
///     the same place) still fills blanks only and never overwrites text.
///
///     But a RELOCATION -- a new point more than SamePlaceToleranceMeters away --
///     replaces the address block outright, because text describing the previous
///     point is not a refinement, it is wrong. "Replaces" includes CLEARING a
///     field the provider has no value for: a postcode the new place did not
///     supply is not evidence the old postcode still applies. The original rule could not tell
///     hand-typed text from text an EARLIER geocode wrote, so typing coordinates
///     in Vile Parle and then searching a venue in Airoli kept the Vile Parle
///     address next to Airoli coordinates and saved that as one venue.
///
///     Name is exempt and always fills blanks only: it is the admin's chosen
///     label for the venue, not a description of the point.
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
    /// How far a new point can be from the current one and still count as the
    /// same place. Picking the same venue twice, or dragging the pin to the exact
    /// gate, lands well inside this; a different venue is nearly always far
    /// outside it. Chosen loose enough that a geocoder returning the car park
    /// instead of the entrance is not treated as a move.
    /// </summary>
    public const double SamePlaceToleranceMeters = 100d;

    /// <summary>
    /// True when the incoming point is a different place rather than a refinement
    /// of the current one. No current coordinates means this is the first fill,
    /// which is never a relocation.
    /// </summary>
    private static bool IsRelocation(double? fromLat, double? fromLng, double toLat, double toLng)
        => fromLat is not null
        && fromLng is not null
        && DistanceMeters(fromLat.Value, fromLng.Value, toLat, toLng) > SamePlaceToleranceMeters;

    /// <summary>Haversine great-circle distance in metres.</summary>
    private static double DistanceMeters(double lat1, double lng1, double lat2, double lng2)
    {
        const double earthRadiusMetres = 6_371_000d;

        var dLat = (lat2 - lat1) * Math.PI / 180d;
        var dLng = (lng2 - lng1) * Math.PI / 180d;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
              + Math.Cos(lat1 * Math.PI / 180d) * Math.Cos(lat2 * Math.PI / 180d)
              * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);

        return earthRadiusMetres * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

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

        var lat = (double)suggestion.Latitude;
        var lng = (double)suggestion.Longitude;

        // Picking a suggestion far from the current point means the admin has
        // identified a DIFFERENT venue, so the address block on screen describes
        // somewhere else and must go.
        var relocated = IsRelocation(current.Latitude, current.Longitude, lat, lng);

        return current with
        {
            // Rule 1 — unconditional.
            Latitude  = lat,
            Longitude = lng,

            // ShortAddress is the exception among the text fields: it exists so
            // the admin never has to compose it, so the provider's version always
            // replaces whatever is there.
            ShortAddress = Truncate(suggestion.ShortAddress, ShortAddressMaxLength),

            // Name is the admin's label for the venue, never a description of the
            // point, so it fills blanks only even when relocating.
            Name = Fill(current.Name, suggestion.Name, NameMaxLength, replace: false),

            // Rule 2 — blanks only when refining, replaced when relocating.
            AddressLine1 = Fill(
                current.AddressLine1,
                string.IsNullOrWhiteSpace(suggestion.FullAddress) ? suggestion.Name : suggestion.FullAddress,
                AddressLineMaxLength, relocated),
            City       = Fill(current.City,       suggestion.City,       CityMaxLength,       relocated),
            PostalCode = Fill(current.PostalCode, suggestion.PostalCode, PostalCodeMaxLength, relocated),
            Country    = Fill(current.Country,    suggestion.Country,    CountryMaxLength,    relocated),
            State      = NextState(current.State, suggestion.State, canonicalStates, relocated),
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

        // Judged BEFORE the coordinates are overwritten. Nudging the pin to the
        // exact gate is a refinement and must not touch typed text; clicking
        // across the city, or typing a whole new coordinate pair, is a relocation
        // and the old address block is then simply wrong.
        var relocated = IsRelocation(current.Latitude, current.Longitude, latitude, longitude);

        var moved = current with { Latitude = latitude, Longitude = longitude };

        // No address at that point (middle of a field, provider down) is a
        // survivable outcome: keep the coordinates, skip the labels.
        if (detail is null) return moved;

        return moved with
        {
            AddressLine1 = Fill(moved.AddressLine1, detail.Address,    AddressLineMaxLength, relocated),
            City         = Fill(moved.City,         detail.City,       CityMaxLength,        relocated),
            PostalCode   = Fill(moved.PostalCode,   detail.PostalCode, PostalCodeMaxLength,  relocated),
            Country      = Fill(moved.Country,      detail.Country,    CountryMaxLength,     relocated),
            State        = NextState(moved.State, detail.State, canonicalStates, relocated),
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

    /// <summary>
    /// Fill a text field from the provider.
    ///
    /// Refining: blanks are filled, existing text is kept, and a provider with
    /// nothing to offer never wipes a value that is already there.
    ///
    /// Relocating: the provider's answer REPLACES the field -- including when the
    /// provider has no value for it, which CLEARS it. That last part is the whole
    /// point. Indian addresses from Nominatim routinely come back without a
    /// postcode, so keeping the previous one left a Vile Parle 400057 sitting
    /// under a Thane address, looking deliberate and saving without complaint. An
    /// empty box the admin must fill is honest; a plausible wrong one is not.
    /// This matches how NextState already treats an unmatchable state.
    /// </summary>
    private static string? Fill(string? currentValue, string? incoming, int maxLength, bool replace)
    {
        if (replace)
            return string.IsNullOrWhiteSpace(incoming) ? null : Truncate(incoming!, maxLength);

        if (!string.IsNullOrWhiteSpace(currentValue)) return currentValue;

        return string.IsNullOrWhiteSpace(incoming) ? currentValue : Truncate(incoming!, maxLength);
    }

    /// <summary>
    /// State needs its own path because it must land on an exact StateSelect
    /// option. When relocating, an unmatchable provider state BLANKS the field
    /// rather than leaving the previous state standing next to a new city — an
    /// empty dropdown the admin must fill is honest, a stale one is not.
    /// </summary>
    private static string? NextState(
        string? currentState,
        string? incomingState,
        IReadOnlyList<string> canonicalStates,
        bool relocated)
    {
        var matched = MatchCanonicalState(incomingState, canonicalStates);

        if (relocated) return matched;
        return string.IsNullOrWhiteSpace(currentState) ? matched ?? currentState : currentState;
    }

    /// <summary>
    /// Provider strings routinely exceed our column widths (display_name is often
    /// 150+ chars). Trim here so a save fails on real validation problems, not on
    /// a length the admin never chose.
    /// </summary>
    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];
}
