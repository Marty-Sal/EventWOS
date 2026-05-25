using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EventWOS.BlazorWeb.Services;

// ─── DTOs ─────────────────────────────────────────────────────────────────────

public sealed record ManagerPermissionRecord(
    Guid     GrantId,
    Guid     PermissionId,
    string   Name,
    string   Resource,
    string   Action,
    string?  Description,
    bool     IsActive,
    DateTime? ExpiresAt,
    DateTime GrantedAt
);

public sealed record ManagerRecord(
    Guid     Id,
    string   Mobile,
    string   FullName,
    string?  Email,
    string?  AvatarUrl,
    string   Status,
    DateTime? LastLoginAt,
    DateTime CreatedAt,
    IReadOnlyList<ManagerPermissionRecord> Permissions
);

public sealed record PermissionDef(
    Guid    Id,
    string  Name,
    string  Resource,
    string  Action,
    string? Description
);

public sealed record PagedManagerResult(
    IReadOnlyList<ManagerRecord> Items,
    int  TotalCount,
    int  PageNumber,
    int  PageSize,
    bool HasNextPage,
    bool HasPreviousPage
);

// ─── Interface ────────────────────────────────────────────────────────────────

public interface IManagerApiService
{
    Task<PagedManagerResult?> GetManagersAsync(int page = 1, string? search = null, CancellationToken ct = default);
    Task<IReadOnlyList<PermissionDef>?> GetAllPermissionsAsync(CancellationToken ct = default);
    Task<(bool Ok, ManagerRecord? Data, string? Error)> CreateManagerAsync(string mobile, string fullName, string? email, CancellationToken ct = default);
    Task<(bool Ok, ManagerPermissionRecord? Data, string? Error)> GrantPermissionAsync(Guid managerId, Guid permissionId, DateTime? expiresAt, CancellationToken ct = default);
    Task<(bool Ok, string? Error)> RevokePermissionAsync(Guid managerId, Guid grantId, CancellationToken ct = default);
    Task<(bool Ok, string? Error)> ChangeStatusAsync(Guid managerId, string status, CancellationToken ct = default);
}

// ─── Implementation ───────────────────────────────────────────────────────────

public sealed class ManagerApiService : IManagerApiService
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public ManagerApiService(HttpClient http) => _http = http;

    public async Task<PagedManagerResult?> GetManagersAsync(int page = 1, string? search = null, CancellationToken ct = default)
    {
        try
        {
            var url = $"api/v1/managers?page={page}&pageSize=20";
            if (!string.IsNullOrWhiteSpace(search)) url += $"&search={Uri.EscapeDataString(search)}";
            var r = await _http.GetFromJsonAsync<ApiResult<PagedManagerResult>>(url, _json, ct);
            return r?.Data;
        }
        catch { return null; }
    }

    public async Task<IReadOnlyList<PermissionDef>?> GetAllPermissionsAsync(CancellationToken ct = default)
    {
        try
        {
            var r = await _http.GetFromJsonAsync<ApiResult<IReadOnlyList<PermissionDef>>>(
                "api/v1/managers/permissions/all", _json, ct);
            return r?.Data;
        }
        catch { return null; }
    }

    public async Task<(bool Ok, ManagerRecord? Data, string? Error)> CreateManagerAsync(
        string mobile, string fullName, string? email, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync("api/v1/managers", new { mobile, fullName, email }, ct);
            var raw  = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode)
            {
                var body = JsonSerializer.Deserialize<ApiResult<ManagerRecord>>(raw, _json);
                return (true, body?.Data, null);
            }
            var err = string.IsNullOrWhiteSpace(raw) ? null :
                JsonSerializer.Deserialize<ApiResult<object>>(raw, _json);
            return (false, null, err?.Errors?.FirstOrDefault() ?? "Failed to create manager.");
        }
        catch (Exception ex) { return (false, null, ex.Message); }
    }

    public async Task<(bool Ok, ManagerPermissionRecord? Data, string? Error)> GrantPermissionAsync(
        Guid managerId, Guid permissionId, DateTime? expiresAt, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync(
                $"api/v1/managers/{managerId}/permissions",
                new { permissionId, expiresAt }, ct);
            var raw = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode)
            {
                var body = JsonSerializer.Deserialize<ApiResult<ManagerPermissionRecord>>(raw, _json);
                return (true, body?.Data, null);
            }
            var err = string.IsNullOrWhiteSpace(raw) ? null :
                JsonSerializer.Deserialize<ApiResult<object>>(raw, _json);
            return (false, null, err?.Errors?.FirstOrDefault() ?? "Failed to grant permission.");
        }
        catch (Exception ex) { return (false, null, ex.Message); }
    }

    public async Task<(bool Ok, string? Error)> RevokePermissionAsync(
        Guid managerId, Guid grantId, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.DeleteAsync($"api/v1/managers/{managerId}/permissions/{grantId}", ct);
            if (resp.IsSuccessStatusCode) return (true, null);
            var raw = await resp.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(raw)) return (false, "Request failed.");
            var err = JsonSerializer.Deserialize<ApiResult<object>>(raw, _json);
            return (false, err?.Errors?.FirstOrDefault() ?? "Failed to revoke.");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public async Task<(bool Ok, string? Error)> ChangeStatusAsync(
        Guid managerId, string status, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PatchAsJsonAsync(
                $"api/v1/managers/{managerId}/status", new { status }, ct);
            if (resp.IsSuccessStatusCode) return (true, null);
            var raw = await resp.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(raw)) return (false, "Request failed.");
            var err = JsonSerializer.Deserialize<ApiResult<object>>(raw, _json);
            return (false, err?.Errors?.FirstOrDefault() ?? "Failed.");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }
}
