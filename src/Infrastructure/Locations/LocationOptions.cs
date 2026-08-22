namespace EventWOS.Infrastructure.Locations;

/// <summary>
/// Bound from the "LocationProvider" configuration section (appsettings or
/// environment variables). Every knob a provider needs lives here so that
/// swapping providers is a config change, and so no credential is ever
/// hard-coded next to the HTTP call.
///
/// Environment-variable form (Railway etc.) uses the standard
/// double-underscore nesting, e.g. LocationProvider__ApiKey=xxx.
/// </summary>
public sealed class LocationOptions
{
    public const string SectionName = "LocationProvider";

    /// <summary>
    /// Selects the implementation at startup. "Nominatim" is the only provider
    /// shipped today; the DI switch throws on an unknown value rather than
    /// silently falling back, so a typo in production config fails loudly at
    /// boot instead of quietly disabling venue search.
    /// </summary>
    public string Provider { get; set; } = "Nominatim";

    public string BaseUrl { get; set; } = "https://nominatim.openstreetmap.org/";

    /// <summary>
    /// Nominatim's usage policy REQUIRES a genuine identifying User-Agent with
    /// contact info; requests with a generic or absent UA get blocked. Google
    /// and Mappls ignore this field.
    /// </summary>
    public string UserAgent { get; set; } = "EventWOS/1.0 (support@eventwos.app)";

    public int TimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// API key / token for providers that need one (Google, Mappls). Empty for
    /// Nominatim. Server-side only — never sent to the browser: Blazor talks to
    /// our own /api/v1/locations endpoints, which proxy to the provider.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Shortest query we'll forward to the provider. Below this the service
    /// returns empty without an HTTP call — one-character queries produce
    /// useless results and burn rate-limit budget.
    /// </summary>
    public int MinQueryLength { get; set; } = 3;

    public int MaxResults { get; set; } = 8;

    /// <summary>
    /// How long identical lookups are served from memory. Venue searches are
    /// extremely repetitive (every admin types "mumbai"), and Nominatim's
    /// public instance allows only ~1 request/second, so caching is the
    /// difference between usable and rate-limited. Set to 0 to disable.
    /// </summary>
    public int CacheMinutes { get; set; } = 30;

    /// <summary>
    /// Optional ISO 3166-1 alpha-2 bias, e.g. "in". Narrows suggestions to the
    /// countries the business actually operates in. Null/empty = worldwide.
    /// </summary>
    public string? CountryCodes { get; set; } = "in";
}
