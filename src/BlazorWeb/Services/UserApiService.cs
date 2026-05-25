using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

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
    Task<(bool Ok, string? Error)> CreateVendorAsync(string mobile, string fullName, string? businessName, string? email, CancellationToken ct = default);
    Task<(bool Ok, string? Error)> CreateCrewAsync(string mobile, string fullName, string? email, string? referralCode, CancellationToken ct = default);
}

public sealed class UserApiService : IUserApiService
{
    private readonly HttpClient _http;

    // Handles both string and integer enum values from the API
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(), new FlexibleEnumStringConverter() }
    };

    public UserApiService(HttpClient http) => _http = http;

    public async Task<UserProfileDto?> GetMeAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetFromJsonAsync<ApiResult<UserProfileDto>>("api/v1/users/me", JsonOpts, ct);
            return resp?.Data;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[UserApiService] GetMeAsync error: {ex.Message}");
            return null;
        }
    }

    public async Task<PagedResult<UserListItemDto>?> GetUsersAsync(
        int page = 1, int pageSize = 20, string? search = null, CancellationToken ct = default)
    {
        try
        {
            var url = $"api/v1/users?page={page}&pageSize={pageSize}";
            if (!string.IsNullOrEmpty(search)) url += $"&search={Uri.EscapeDataString(search)}";
            var resp = await _http.GetFromJsonAsync<ApiResult<PagedResult<UserListItemDto>>>(url, JsonOpts, ct);
            return resp?.Data;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[UserApiService] GetUsersAsync error: {ex.Message}");
            return null;
        }
    }

    public async Task<bool> ChangeStatusAsync(Guid userId, string status, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PatchAsJsonAsync($"api/v1/users/{userId}/status", new { status }, ct);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<bool> UpdateProfileAsync(string fullName, string? email, string? avatarUrl, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PutAsJsonAsync("api/v1/users/me", new { fullName, email, avatarUrl }, ct);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }
}

/// <summary>
/// Converter that reads both numeric (0,1,2) and string ("Admin","Manager") enum values
/// and stores them as their string name. Handles APIs that may send either format.
/// </summary>
public sealed class FlexibleEnumStringConverter : JsonConverter<string>
{
    public override bool CanConvert(Type typeToConvert) => false; // used only explicitly if needed
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetString();
    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        => writer.WriteStringValue(value);
    public async Task<(bool Ok, string? Error)> CreateVendorAsync(
        string mobile, string fullName, string? businessName, string? email, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync("api/v1/vendors",
                new { mobile, fullName, businessName, email }, ct);
            if (resp.IsSuccessStatusCode) return (true, null);
            var body = await resp.Content.ReadAsStringAsync(ct);
            var parsed = JsonSerializer.Deserialize<ApiResult<object>>(body, JsonOpts);
            return (false, parsed?.Errors?.FirstOrDefault() ?? "Failed to create vendor.");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public async Task<(bool Ok, string? Error)> CreateCrewAsync(
        string mobile, string fullName, string? email, string? referralCode, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync("api/v1/crew",
                new { mobile, fullName, email, referralCode }, ct);
            if (resp.IsSuccessStatusCode) return (true, null);
            var body = await resp.Content.ReadAsStringAsync(ct);
            var parsed = JsonSerializer.Deserialize<ApiResult<object>>(body, JsonOpts);
            return (false, parsed?.Errors?.FirstOrDefault() ?? "Failed to create crew.");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

}
