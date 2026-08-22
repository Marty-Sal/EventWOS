using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace EventWOS.BlazorWeb.Services;

/// <summary>
/// Client for /api/v1/locations/*. The browser never talks to OpenStreetMap
/// (or Google/Mappls) directly — it goes through our API, which is what keeps
/// provider credentials server-side and lets the server cache and rate-limit.
/// Because of that, this class has no idea which provider is configured.
/// </summary>
public interface ILocationApiService
{
    Task<IReadOnlyList<LocationSuggestion>> SearchAsync(string query, CancellationToken ct = default);

    Task<LocationDetail?> ReverseGeocodeAsync(decimal latitude, decimal longitude, CancellationToken ct = default);
}

public sealed class LocationApiService : ILocationApiService
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private sealed record ApiResult<T>(bool Success, T? Data, string? Message);

    public LocationApiService(HttpClient http) => _http = http;

    public async Task<IReadOnlyList<LocationSuggestion>> SearchAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<LocationSuggestion>();

        try
        {
            var r = await _http.GetFromJsonAsync<ApiResult<List<LocationSuggestion>>>(
                $"api/v1/locations/search?q={Uri.EscapeDataString(query.Trim())}", JsonOpts, ct);
            return r?.Data ?? (IReadOnlyList<LocationSuggestion>)Array.Empty<LocationSuggestion>();
        }
        catch (OperationCanceledException)
        {
            // Debounce cancelled this request — rethrow so the caller can leave
            // the previous suggestions on screen instead of blanking the list.
            throw;
        }
        catch
        {
            // Search is an assistive feature. If it's unavailable the admin can
            // still type coordinates by hand, so a failure must not break the
            // form or raise a toast on every keystroke.
            return Array.Empty<LocationSuggestion>();
        }
    }

    public async Task<LocationDetail?> ReverseGeocodeAsync(
        decimal latitude, decimal longitude, CancellationToken ct = default)
    {
        try
        {
            // InvariantCulture: the query string must use '.' regardless of the
            // browser's locale, or a comma-decimal user sends "19,105" and the
            // server reads two parameters.
            var url = "api/v1/locations/reverse"
                    + $"?latitude={latitude.ToString(CultureInfo.InvariantCulture)}"
                    + $"&longitude={longitude.ToString(CultureInfo.InvariantCulture)}";

            var r = await _http.GetFromJsonAsync<ApiResult<LocationDetail?>>(url, JsonOpts, ct);
            return r?.Data;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // No address is a survivable outcome: the dragged coordinates are
            // what matter for the geofence, the address text is a convenience.
            return null;
        }
    }
}
