namespace EventWOS.Application.Notifications.Services;

/// <summary>
/// Turns whatever is stored on a user into the E.164-style digits WhatsApp
/// expects. The app stores bare 10-digit Indian mobiles, but real data also
/// contains "+91 98765 43210", "091-98765-43210" and "919876543210", because it
/// arrives from registration forms, admin entry and imports.
///
/// Providers reject anything non-numeric outright, and a wrongly prefixed number
/// is worse than a rejection: it can be a real number belonging to someone else.
/// </summary>
public static class WhatsAppNumber
{
    /// <summary>Length of an Indian subscriber number without its country code.</summary>
    private const int LocalLength = 10;

    /// <summary>
    /// Returns digits only, with the country code applied, or null when the input
    /// cannot be trusted to be a phone number.
    /// </summary>
    public static string? Normalize(string? mobile, string defaultCountryCode = "91")
    {
        if (string.IsNullOrWhiteSpace(mobile)) return null;

        var digits = new string(mobile.Where(char.IsDigit).ToArray());
        if (digits.Length == 0) return null;

        var cc = new string(defaultCountryCode.Where(char.IsDigit).ToArray());
        if (cc.Length == 0) cc = "91";

        // Leading zeros are a domestic trunk prefix ("098765..."), never part of
        // the international number.
        digits = digits.TrimStart('0');

        if (digits.Length == LocalLength)
            return cc + digits;

        // Already carries the country code.
        if (digits.Length == cc.Length + LocalLength && digits.StartsWith(cc, StringComparison.Ordinal))
            return digits;

        // Plausible international number from another country -- pass through
        // rather than mangling it with an Indian prefix.
        if (digits.Length is >= 11 and <= 15)
            return digits;

        // Too short or absurdly long: refuse instead of guessing. The delivery is
        // skipped with a reason, which is visible, unlike a message sent to a
        // number the user never had.
        return null;
    }
}
