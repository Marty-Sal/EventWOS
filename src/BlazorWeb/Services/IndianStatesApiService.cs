using System.Net.Http.Json;
using System.Text.Json;

namespace EventWOS.BlazorWeb.Services;

public sealed record IndianStateOption(string Name, bool IsUnionTerritory);

/// <summary>
/// The canonical India states + union territories list, used by every
/// "State" dropdown in the app. Static reference data — fetched once per
/// browser tab and cached in memory (AddScoped behaves like a singleton
/// for a WASM app's lifetime, so this cache is shared across every page
/// that injects this service, including anonymous pre-login pages like
/// vendor self-registration).
/// </summary>
public interface IIndianStatesApiService
{
    Task<IReadOnlyList<IndianStateOption>> GetStatesAsync(CancellationToken ct = default);
}

public sealed class IndianStatesApiService : IIndianStatesApiService
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private Task<IReadOnlyList<IndianStateOption>>? _cache;

    private sealed record ApiResult<T>(bool Success, T? Data, string? Message);

    public IndianStatesApiService(HttpClient http) => _http = http;

    public Task<IReadOnlyList<IndianStateOption>> GetStatesAsync(CancellationToken ct = default)
    {
        _cache ??= FetchAsync(ct);
        return _cache;
    }

    private async Task<IReadOnlyList<IndianStateOption>> FetchAsync(CancellationToken ct)
    {
        try
        {
            var resp = await _http.GetFromJsonAsync<ApiResult<List<IndianStateOption>>>(
                "api/v1/lookups/indian-states", JsonOpts, ct);
            return (IReadOnlyList<IndianStateOption>?)resp?.Data ?? Array.Empty<IndianStateOption>();
        }
        catch
        {
            // Allow a retry on the next call instead of caching a permanent failure.
            _cache = null;
            return Array.Empty<IndianStateOption>();
        }
    }
}
