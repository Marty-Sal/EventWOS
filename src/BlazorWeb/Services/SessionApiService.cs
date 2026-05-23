using System.Net.Http.Json;
using System.Text.Json;

namespace EventWOS.BlazorWeb.Services;

public sealed record SessionDto(
    Guid Id, Guid SessionId, string DeviceId, string DeviceName,
    string IpAddress, DateTime LastActivityAt, bool IsActive, DateTime CreatedAt);

public interface ISessionApiService
{
    Task<IReadOnlyList<SessionDto>> GetSessionsAsync(CancellationToken ct = default);
    Task<bool> RevokeSessionAsync(Guid sessionRecordId, CancellationToken ct = default);
}

public sealed class SessionApiService : ISessionApiService
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public SessionApiService(HttpClient http) => _http = http;

    public async Task<IReadOnlyList<SessionDto>> GetSessionsAsync(CancellationToken ct = default)
    {
        var resp = await _http.GetFromJsonAsync<ApiResult<IReadOnlyList<SessionDto>>>("api/v1/sessions", JsonOpts, ct);
        return resp?.Data ?? Array.Empty<SessionDto>();
    }

    public async Task<bool> RevokeSessionAsync(Guid sessionRecordId, CancellationToken ct = default)
    {
        var resp = await _http.DeleteAsync($"api/v1/sessions/{sessionRecordId}", ct);
        return resp.IsSuccessStatusCode;
    }
}
