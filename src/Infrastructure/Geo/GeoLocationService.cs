using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using EventWOS.Application.Attendance.Geo;
using Microsoft.Extensions.Logging;

namespace EventWOS.Infrastructure.Geo;

/// <summary>
/// Reverse-geocodes coordinates via OpenStreetMap\'s Nominatim service.
///
/// Nominatim usage policy summary (see
/// https://operations.osmfoundation.org/policies/nominatim/):
///   • Max 1 request per second per unique IP.
///   • A valid identifying User-Agent is REQUIRED — no browsers, no
///     "curl", no blanks. We send "EventWOS/1.0 (contact via
///     admin@eventwos.local)". Update the contact if we ever ship a
///     public support address.
///   • No bulk / batch geocoding of large datasets. Our workload is
///     one lookup per QR check-in — far under any reasonable interpretation.
///   • Attribution: "© OpenStreetMap contributors" — surfaced in the
///     app footer / about page separately (LocationPin does not need
///     to render it at each row).
///
/// Design choices:
///   • Sync (awaited) call on the check-in code path with a 2-second
///     HTTP timeout. Nominatim is typically 300-600 ms; the 2 s cap
///     protects the check-in UX during their slow moments. On timeout
///     we persist coords only — the address stays NULL, the pin still
///     works, and a background retry could fill it in later if we
///     ever build one.
///   • Per-second in-process throttle (SemaphoreSlim + interval gate).
///     For a single API instance this suffices; if we ever run more
///     than one replica, we would move the throttle to Redis. Not
///     worth the complexity today.
///   • 24-hour in-memory LRU cache keyed by "lat,lng" truncated to 4
///     decimal places (~11 m grid). Repeat check-ins at the same
///     venue skip the network entirely. Bounded at 5000 entries so
///     the container memory footprint stays flat.
///   • Never throws. Any transport / parse failure is logged at
///     warning and returns Address = null. Coords come from the input,
///     not from Nominatim, so they\'re always returned when parseable.
/// </summary>
public sealed class GeoLocationService : IGeoLocationService, IDisposable
{
    private static readonly HttpClient _http = CreateClient();

    // Rate-limit gate (1 req/s). Combined with the cache this keeps our
    // effective outbound rate well under Nominatim\'s ceiling even
    // during a rush.
    private static readonly SemaphoreSlim _gate = new(1, 1);
    private static DateTime _lastCallUtc = DateTime.MinValue;
    private static readonly TimeSpan _minInterval = TimeSpan.FromMilliseconds(1100);

    // Tiny in-memory cache. ConcurrentDictionary + a naive size cap —
    // when the dictionary crosses 5000 keys we just clear the oldest
    // half by sampling. Good enough for our scale; simpler than a real
    // LRU implementation and doesn\'t pull in a caching package.
    private static readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private const int CacheMaxSize = 5000;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    private readonly ILogger<GeoLocationService> _log;
    public GeoLocationService(ILogger<GeoLocationService> log) => _log = log;

    private static HttpClient CreateClient()
    {
        var c = new HttpClient
        {
            BaseAddress = new Uri("https://nominatim.openstreetmap.org/"),
            Timeout     = TimeSpan.FromSeconds(2),
        };
        // Nominatim REQUIRES an identifying User-Agent. Requests without
        // one are blocked with HTTP 403.
        c.DefaultRequestHeaders.UserAgent.ParseAdd(
            "EventWOS/1.0 (contact: admin@eventwos.local)");
        c.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");
        return c;
    }

    public async Task<(string? Coords, string? Address)> LookupAsync(string? raw, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (null, null);
        if (raw.StartsWith("unavailable", StringComparison.OrdinalIgnoreCase))
            return (null, null);

        // Accept both "lat,lng" and legacy "lat,lng|Address" (idempotent
        // — if the address is already carried through, we honour it and
        // skip the network hop entirely).
        var pipeIdx = raw.IndexOf('|');
        string coordsPart = pipeIdx >= 0 ? raw[..pipeIdx] : raw;
        string? preExistingAddress = pipeIdx >= 0 ? raw[(pipeIdx + 1)..] : null;

        var parts = coordsPart.Split(',', 2);
        if (parts.Length != 2) return (null, null);
        if (!double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)) return (null, null);
        if (!double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var lng)) return (null, null);
        if (lat is < -90 or > 90 || lng is < -180 or > 180) return (null, null);

        // Canonical coord string — 6 dp, no whitespace.
        var coords = $"{lat.ToString("0.######", CultureInfo.InvariantCulture)}," +
                     $"{lng.ToString("0.######", CultureInfo.InvariantCulture)}";

        if (!string.IsNullOrWhiteSpace(preExistingAddress))
            return (coords, preExistingAddress);

        // Cache key at 4 dp → ~11 m grid. Two check-ins from adjacent
        // corners of a venue hall share a key.
        var cacheKey = $"{lat.ToString("0.####", CultureInfo.InvariantCulture)}," +
                       $"{lng.ToString("0.####", CultureInfo.InvariantCulture)}";
        if (_cache.TryGetValue(cacheKey, out var hit) && hit.ExpiresUtc > DateTime.UtcNow)
            return (coords, hit.Address);

        string? address = await CallNominatimAsync(lat, lng, ct);

        // Store in cache even if null — avoids hammering Nominatim for
        // known-bad or offline coords. The cache TTL is short enough
        // that a genuinely transient error clears itself.
        if (_cache.Count >= CacheMaxSize) EvictHalf();
        _cache[cacheKey] = new CacheEntry(address, DateTime.UtcNow.Add(CacheTtl));

        return (coords, address);
    }

    private async Task<string?> CallNominatimAsync(double lat, double lng, CancellationToken ct)
    {
        try
        {
            // Rate-limit gate — serialises outbound calls to ~1/sec.
            await _gate.WaitAsync(ct);
            try
            {
                var wait = _minInterval - (DateTime.UtcNow - _lastCallUtc);
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                _lastCallUtc = DateTime.UtcNow;
            }
            finally { _gate.Release(); }

            var url = $"reverse?format=jsonv2" +
                      $"&lat={lat.ToString("0.######", CultureInfo.InvariantCulture)}" +
                      $"&lon={lng.ToString("0.######", CultureInfo.InvariantCulture)}" +
                      $"&zoom=14&addressdetails=1";

            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("Nominatim reverse returned {Status} for ({Lat},{Lng})",
                    (int)resp.StatusCode, lat, lng);
                return null;
            }

            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (!doc.RootElement.TryGetProperty("address", out var addr))
                return null;

            // Build "Locality, City" (or the best available pair) —
            // Nominatim\'s address block has many optional fields; we
            // pick the two most human-recognisable ones for India-ish
            // usage. The suburb/neighbourhood field is preferred over
            // any admin field because it matches how people describe
            // where they are ("Airoli, Navi Mumbai" not "Airoli,
            // Thane, Maharashtra").
            string? primary   = TryGet(addr, "suburb", "neighbourhood", "hamlet", "village", "town", "quarter", "city_district");
            string? secondary = TryGet(addr, "city", "town", "municipality", "county");

            // If primary == secondary (small towns Nominatim reports
            // twice), collapse and step out to the state.
            if (!string.IsNullOrWhiteSpace(primary) &&
                string.Equals(primary, secondary, StringComparison.OrdinalIgnoreCase))
                secondary = TryGet(addr, "state_district", "state", "country");

            var parts = new[] { primary, secondary }
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .ToArray();

            return parts.Length > 0 ? string.Join(", ", parts) : null;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            // HttpClient timeout — expected under load. Log at info, not warn.
            _log.LogInformation("Nominatim reverse timed out for ({Lat},{Lng})", lat, lng);
            return null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Nominatim reverse failed for ({Lat},{Lng})", lat, lng);
            return null;
        }
    }

    private static string? TryGet(JsonElement addr, params string[] keys)
    {
        foreach (var k in keys)
            if (addr.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
        return null;
    }

    private static void EvictHalf()
    {
        // Not a true LRU — just samples half the keys and drops them.
        // Cheap, bounded, and preserves the hot set enough for our
        // usage pattern (repeat check-ins from the same venue).
        var toRemove = _cache.Keys.Take(_cache.Count / 2).ToList();
        foreach (var k in toRemove) _cache.TryRemove(k, out _);
    }

    public void Dispose() { /* _http is static; nothing per-instance. */ }

    private sealed record CacheEntry(string? Address, DateTime ExpiresUtc);
}
