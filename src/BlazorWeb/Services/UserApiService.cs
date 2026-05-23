using System.Net.Http.Json;
using System.Text.Json;

namespace EventWOS.BlazorWeb.Services;

public sealed record UserProfileDto(
    Guid Id, string Mobile, string FullName, string? Email,
    string? AvatarUrl, string Role, string Status,
    IReadOnlyList<string> Permissions, DateTime? LastLoginAt);

public sealed record UserListItemDto(
    Guid Id, string Mobile, string FullName, string? Email,
    string Role, string Status, DateTime CreatedAt);

public sealed record PagedResult<T>(
    IReadOnlyList<T> Items, int TotalCount, int PageNumber, int PageSize, int TotalPages);

public interface IUserApiService
{
    Task<UserProfileDto?> GetMeAsync(CancellationToken ct = default);
    Task<PagedResult<UserListItemDto>?> GetUsersAsync(int page = 1, int pageSize = 20, string? search = null, CancellationToken ct = default);
    Task<bool> ChangeStatusAsync(Guid userId, string status, CancellationToken ct = default);
    Task<bool> UpdateProfileAsync(string fullName, string? email, string? avatarUrl, CancellationToken ct = default);
}

public sealed class UserApiService : IUserApiService
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public UserApiService(HttpClient http) => _http = http;

    public async Task<UserProfileDto?> GetMeAsync(CancellationToken ct = default)
    {
        var resp = await _http.GetFromJsonAsync<ApiResult<UserProfileDto>>("api/v1/users/me", JsonOpts, ct);
        return resp?.Data;
    }

    public async Task<PagedResult<UserListItemDto>?> GetUsersAsync(
        int page = 1, int pageSize = 20, string? search = null, CancellationToken ct = default)
    {
        var url = $"api/v1/users?page={page}&pageSize={pageSize}";
        if (!string.IsNullOrEmpty(search)) url += $"&search={Uri.EscapeDataString(search)}";
        var resp = await _http.GetFromJsonAsync<ApiResult<PagedResult<UserListItemDto>>>(url, JsonOpts, ct);
        return resp?.Data;
    }

    public async Task<bool> ChangeStatusAsync(Guid userId, string status, CancellationToken ct = default)
    {
        var resp = await _http.PatchAsJsonAsync($"api/v1/users/{userId}/status", new { status }, ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> UpdateProfileAsync(string fullName, string? email, string? avatarUrl, CancellationToken ct = default)
    {
        var resp = await _http.PutAsJsonAsync("api/v1/users/me", new { fullName, email, avatarUrl }, ct);
        return resp.IsSuccessStatusCode;
    }
}
