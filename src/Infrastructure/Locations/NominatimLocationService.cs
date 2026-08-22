using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using EventWOS.Application.Locations;
using EventWOS.Application.Locations.DTOs;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EventWOS.Infrastructure.Locations;

/// <summary>
/// <see cref="ILocationService"/> over OpenStreetMap's Nominatim.
///
/// Chosen as the first provider because it needs no API key, no billing
/// account and no contract — EventWOS can ship venue search on day one. The
/// trade-off is the public instance's ~1 request/second policy, which is why
/// this class leans hard on caching and a minimum query length.
///
/// Everything Nominatim-shaped is contained in this file: the JSON field
/// names, the "jsonv2" format quirks, the address-component mapping. The
/// application layer only ever sees the neutral DTOs, so a GoogleLocationService
/// can be dropped in beside this one without touching a handler.
///
/// Failure policy (see the interface docs): provider problems degrade to empty
/// results, never exceptions. Caller cancellation is the one thing that IS
/// propagated, because the debounced search box relies on abandoning
/// superseded requests.
/// </summary>
public sealed class NominatimLocationService : ILocationService
{
    private readonly HttpClient      _http;
    private readonly IMemoryCache    _cache;
    private readonly LocationOptions _options;
    private readonly ILogger<NominatimLocationService> _logger;

    // Strip control characters and collapse whitespace. Nominatim is a GET API,
    // so the real injection risk is header/URL splitting via CR/LF rather than
    // SQL — and query values are URL-encoded on the way out regardless.
    private static readonly Regex ControlChars = new(@"[\p{C}]+", RegexOptions.Compiled);
    private static readonly Regex MultiSpace   = new(@"\s{2,}",   RegexOptions.Compiled);

    private const int MaxQueryLength = 200;

    public NominatimLocationService(
        HttpClient http,
        IMemoryCache cache,
        IOptions<LocationOptions> options,
        ILogger<NominatimLocationService> logger)
    {
        _options = options.Value;
        _cache   = cache;
        _logger  = logger;
        _http    = http;

        // Configured here rather than in DI so the options are the single source
        // of truth and tests can construct the service with a stub handler.
        if (_http.BaseAddress is null)
            _http.BaseAddress = new Uri(_options.BaseUrl);
        _http.Timeout = TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds));
        if (!_http.DefaultRequestHeaders.Contains("User-Agent"))
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", _options.UserAgent);
    }

    public async Task<IReadOnlyList<LocationSearchResult>> SearchAsync(
        string query, CancellationToken cancellationToken)
    {
        var cleaned = Sanitise(query);

        // Below the floor: answer locally. Not an error — the UI simply hasn't
        // collected enough characters yet.
        if (cleaned.Length < Math.Max(1, _options.MinQueryLength))
            return Array.Empty<LocationSearchResult>();

        var cacheKey = $"loc:search:{cleaned.ToLowerInvariant()}";
        if (TryGetCached<IReadOnlyList<LocationSearchResult>>(cacheKey, out var cached))
            return cached!;

        var url = "search?format=jsonv2&addressdetails=1"
                + $"&limit={Math.Clamp(_options.MaxResults, 1, 20)}"
                + $"&q={Uri.EscapeDataString(cleaned)}";

        if (!string.IsNullOrWhiteSpace(_options.CountryCodes))
            url += $"&countrycodes={Uri.EscapeDataString(_options.CountryCodes.Trim())}";

        var json = await GetJsonAsync(url, cancellationToken, $"search '{cleaned}'");
        if (json is null) return Array.Empty<LocationSearchResult>();

        var results = new List<LocationSearchResult>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return Array.Empty<LocationSearchResult>();

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var mapped = MapSearchResult(el);
                if (mapped is not null) results.Add(mapped);
            }
        }
        catch (JsonException ex)
        {
            // Malformed payload = provider problem, not a caller problem.
            _logger.LogWarning(ex,
                "Location search: could not parse provider response for {Query}.", cleaned);
            return Array.Empty<LocationSearchResult>();
        }

        _logger.LogInformation(
            "Location search for {Query} returned {Count} result(s).", cleaned, results.Count);

        // Cache empty results too: a misspelling gets retyped constantly and
        // re-asking the provider for a known-empty answer wastes rate limit.
        Cache(cacheKey, (IReadOnlyList<LocationSearchResult>)results);
        return results;
    }

    public async Task<LocationDetails?> ReverseGeocodeAsync(
        decimal latitude, decimal longitude, CancellationToken cancellationToken)
    {
        if (!IsValidLatitude(latitude) || !IsValidLongitude(longitude))
        {
            _logger.LogWarning(
                "Reverse geocode rejected out-of-range coordinates {Lat},{Lng}.", latitude, longitude);
            return null;
        }

        // 6 dp (~11 cm) is finer than the marker drag can express, and rounding
        // makes the cache actually hit while the admin nudges the pin.
        var lat = Math.Round(latitude,  6);
        var lng = Math.Round(longitude, 6);

        var cacheKey = $"loc:rev:{lat.ToString(CultureInfo.InvariantCulture)},{lng.ToString(CultureInfo.InvariantCulture)}";
        if (TryGetCached<LocationDetails>(cacheKey, out var cached))
            return cached;

        var url = "reverse?format=jsonv2&addressdetails=1"
                + $"&lat={lat.ToString(CultureInfo.InvariantCulture)}"
                + $"&lon={lng.ToString(CultureInfo.InvariantCulture)}";

        var json = await GetJsonAsync(url, cancellationToken, $"reverse {lat},{lng}");
        if (json is null) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Nominatim signals "nothing here" with an {"error": ...} object.
            if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("error", out _))
            {
                _logger.LogInformation("Reverse geocode found no place at {Lat},{Lng}.", lat, lng);
                return null;
            }

            var address = root.TryGetProperty("address", out var addr) && addr.ValueKind == JsonValueKind.Object
                ? addr
                : default;

            var details = new LocationDetails(
                PlaceId:    ReadString(root, "place_id"),
                Name:       ReadName(root, address),
                Address:    ReadString(root, "display_name"),
                City:       ReadCity(address),
                State:      ReadString(address, "state"),
                PostalCode: ReadString(address, "postcode"),
                Country:    ReadString(address, "country"),
                // Echo back the coordinates we were asked about, NOT the
                // provider's snapped-to-feature centre. The admin dragged the
                // pin to an exact spot and that spot is what gets geofenced —
                // silently moving it to a road centreline would shift the
                // fence out from under them.
                Latitude:   lat,
                Longitude:  lng);

            Cache(cacheKey, details);
            return details;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "Reverse geocode: could not parse provider response for {Lat},{Lng}.", lat, lng);
            return null;
        }
    }

    // ── HTTP plumbing ───────────────────────────────────────────────────────

    /// <summary>
    /// Single place where provider failure becomes "null" instead of an
    /// exception. Returns the raw body on success.
    /// </summary>
    private async Task<string?> GetJsonAsync(
        string url, CancellationToken cancellationToken, string describeForLog)
    {
        // Layer our own timeout on top of the caller's token so a hung provider
        // can't hold a request thread past TimeoutSeconds even if the caller
        // never cancels. HttpClient.Timeout alone surfaces as a bare
        // TaskCanceledException that is hard to tell apart from real
        // cancellation, so we keep an explicit linked source.
        using var timeoutCts = new CancellationTokenSource(
            TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token);

        try
        {
            using var response = await _http.GetAsync(url, linked.Token);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                _logger.LogWarning(
                    "Location provider rate-limited {What} (429). Returning no results.", describeForLog);
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Location provider returned {Status} for {What}.",
                    (int)response.StatusCode, describeForLog);
                return null;
            }

            return await response.Content.ReadAsStringAsync(linked.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Genuine caller cancellation (debounce superseded this request).
            // Propagate so the caller can distinguish "abandoned" from "empty".
            throw;
        }
        catch (OperationCanceledException)
        {
            // Our own timeout fired.
            _logger.LogWarning(
                "Location provider timed out after {Seconds}s for {What}.",
                _options.TimeoutSeconds, describeForLog);
            return null;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex,
                "Location provider unreachable for {What}.", describeForLog);
            return null;
        }
    }

    // ── Mapping ─────────────────────────────────────────────────────────────

    private static LocationSearchResult? MapSearchResult(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;

        var latRaw = ReadString(el, "lat");
        var lonRaw = ReadString(el, "lon");
        if (!TryParseCoordinate(latRaw, out var lat) || !TryParseCoordinate(lonRaw, out var lon))
            return null;
        if (!IsValidLatitude(lat) || !IsValidLongitude(lon)) return null;

        var display = ReadString(el, "display_name") ?? string.Empty;
        var address = el.TryGetProperty("address", out var addr) && addr.ValueKind == JsonValueKind.Object
            ? addr
            : default;

        var name = ReadName(el, address)
                   ?? FirstSegment(display)
                   ?? "Unnamed location";

        return new LocationSearchResult(
            PlaceId:      ReadString(el, "place_id") ?? string.Empty,
            Name:         name,
            ShortAddress: BuildShortAddress(address, display, name),
            FullAddress:  display,
            Latitude:     lat,
            Longitude:    lon);
    }

    /// <summary>
    /// A compact "locality, city, state" label for list rows and the event
    /// screen. Nominatim's display_name is far too long to show in a table
    /// (it repeats the country, postcode and district), so we assemble a
    /// human-scale version from the structured components and only fall back
    /// to truncating display_name when they're missing.
    /// </summary>
    private static string BuildShortAddress(JsonElement address, string display, string name)
    {
        if (address.ValueKind == JsonValueKind.Object)
        {
            var parts = new[]
                {
                    ReadString(address, "suburb")
                        ?? ReadString(address, "neighbourhood")
                        ?? ReadString(address, "village"),
                    ReadCity(address),
                    ReadString(address, "state"),
                }
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p!.Trim())
                // Drop a component that just repeats the venue name.
                .Where(p => !p.Equals(name, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (parts.Count > 0) return string.Join(", ", parts);
        }

        // Fallback: first few segments of display_name, minus the leading name.
        var segments = display.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                              .Where(s => !s.Equals(name, StringComparison.OrdinalIgnoreCase))
                              .Take(3);
        var joined = string.Join(", ", segments);
        return string.IsNullOrWhiteSpace(joined) ? display : joined;
    }

    /// <summary>
    /// City has no single canonical key in OSM data — it depends on the local
    /// administrative vocabulary. Walk the options in decreasing specificity.
    /// </summary>
    private static string? ReadCity(JsonElement address)
        => address.ValueKind != JsonValueKind.Object
            ? null
            : ReadString(address, "city")
              ?? ReadString(address, "town")
              ?? ReadString(address, "municipality")
              ?? ReadString(address, "village")
              ?? ReadString(address, "county")
              ?? ReadString(address, "state_district");

    private static string? ReadName(JsonElement root, JsonElement address)
    {
        var name = ReadString(root, "name");
        if (!string.IsNullOrWhiteSpace(name)) return name!.Trim();

        if (address.ValueKind == JsonValueKind.Object)
        {
            var feature = ReadString(address, "amenity")
                       ?? ReadString(address, "building")
                       ?? ReadString(address, "shop")
                       ?? ReadString(address, "tourism")
                       ?? ReadString(address, "leisure")
                       ?? ReadString(address, "road");
            if (!string.IsNullOrWhiteSpace(feature)) return feature!.Trim();
        }
        return null;
    }

    private static string? FirstSegment(string display)
    {
        if (string.IsNullOrWhiteSpace(display)) return null;
        var idx = display.IndexOf(',');
        var head = idx > 0 ? display[..idx] : display;
        return string.IsNullOrWhiteSpace(head) ? null : head.Trim();
    }

    /// <summary>
    /// Reads a property as a string regardless of whether the provider encoded
    /// it as a JSON string or a number — Nominatim returns place_id as a number
    /// in some versions and a string in others.
    /// </summary>
    private static string? ReadString(JsonElement el, string property)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty(property, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            _                    => null,
        };
    }

    private static bool TryParseCoordinate(string? raw, out decimal value)
        => decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static bool IsValidLatitude(decimal lat)  => lat is >= -90m and <= 90m;
    private static bool IsValidLongitude(decimal lng) => lng is >= -180m and <= 180m;

    // ── Input hygiene & caching ─────────────────────────────────────────────

    private static string Sanitise(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return string.Empty;
        var cleaned = ControlChars.Replace(query, " ");
        cleaned = MultiSpace.Replace(cleaned, " ").Trim();
        if (cleaned.Length > MaxQueryLength) cleaned = cleaned[..MaxQueryLength];
        return cleaned;
    }

    private bool TryGetCached<T>(string key, out T? value)
    {
        value = default;
        if (_options.CacheMinutes <= 0) return false;
        if (!_cache.TryGetValue(key, out var raw) || raw is not T typed) return false;
        value = typed;
        return true;
    }

    private void Cache<T>(string key, T value)
    {
        if (_options.CacheMinutes <= 0) return;
        _cache.Set(key, value, TimeSpan.FromMinutes(_options.CacheMinutes));
    }
}
