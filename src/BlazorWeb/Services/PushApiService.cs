using System.Net.Http.Json;

namespace EventWOS.BlazorWeb.Services;

/// <summary>What the server will allow. Mirrors PushConfigDto.</summary>
public sealed record PushConfigDto(bool Enabled, string? PublicKey, bool RequiresHomeScreenOnIos);

/// <summary>One registered device, for the settings list. Mirrors PushDeviceDto.</summary>
public sealed record PushDeviceDto(
    Guid      Id,
    string    Provider,
    string?   Platform,
    string?   DeviceLabel,
    DateTime? LastSeenAt,
    DateTime? LastSuccessAt,
    bool      IsCurrentDevice = false);

public interface IPushApiService
{
    Task<PushConfigDto?>          GetConfigAsync(CancellationToken ct = default);
    Task<bool>                    SubscribeAsync(string endpoint, string p256dh, string auth, string? platform, CancellationToken ct = default);
    Task<bool>                    UnsubscribeAsync(string? endpoint, Guid? registrationId = null, CancellationToken ct = default);
    Task<IReadOnlyList<PushDeviceDto>> GetDevicesAsync(CancellationToken ct = default);
}

/// <summary>
/// Client for /api/v1/push.
///
/// Reads swallow transport failures and return a neutral result, matching the
/// inbox client: a settings panel that throws would take the page down over a
/// missing badge. Writes return a bool, because the caller has to be able to say
/// "that did not work" instead of showing a toggle that lies about its state.
/// </summary>
public sealed class PushApiService : IPushApiService
{
    private readonly HttpClient _http;
    public PushApiService(HttpClient http) => _http = http;

    public async Task<PushConfigDto?> GetConfigAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _http.GetFromJsonAsync<ApiResult<PushConfigDto>>("api/v1/push/config", ct);
            return result?.Data;
        }
        catch
        {
            // Null means "cannot tell", which the UI renders as unavailable
            // rather than as off -- they are different things to a user.
            return null;
        }
    }

    public async Task<bool> SubscribeAsync(
        string endpoint, string p256dh, string auth, string? platform, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync("api/v1/push/subscribe", new
            {
                endpoint,
                p256dh,
                auth,
                platform
                // No deviceId: the server treats it as advisory only, and the
                // browser has nothing stable to offer that is not a fingerprint.
            }, ct);

            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> UnsubscribeAsync(
        string? endpoint, Guid? registrationId = null, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync("api/v1/push/unsubscribe", new
            {
                endpoint,
                registrationId
            }, ct);

            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<PushDeviceDto>> GetDevicesAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _http.GetFromJsonAsync<ApiResult<List<PushDeviceDto>>>("api/v1/push/devices", ct);
            return result?.Data ?? new List<PushDeviceDto>();
        }
        catch
        {
            return Array.Empty<PushDeviceDto>();
        }
    }
}
