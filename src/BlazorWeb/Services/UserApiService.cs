using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EventWOS.BlazorWeb.Services;

public sealed record UserProfileDto(
    Guid Id, string Username, string Mobile, string FullName, string? Email,
    string? AvatarUrl, string Role, string Status,
    IReadOnlyList<string> Permissions, DateTime? LastLoginAt,
    // Vendor fields
    string? ReferralCode, string? BusinessName, decimal? Rating, int? EventsCompleted, string? InviteMessageTemplate,
    // Crew fields
    decimal? DisciplineScore, int? EventsAttended, decimal? CrewRating, int? CrewRatingCount,
    Guid? VendorId, string? VendorName,
    // Extended profile (self-registered users have these from signup; directly-added
    // Vendor/Crew fill them in here for the first time — see WasDirectlyAdded/ProfileCompleted).
    DateTime? DateOfBirth = null, string? City = null, string? State = null, string? Address = null,
    string? Bio = null, string? Skills = null, int? ExperienceYears = null,
    string? ContactPersonName = null, string? GstNumber = null, string? Website = null,
    // True when an Admin/Vendor directly added this account (skipped the approval queue).
    // When true AND ProfileCompleted is false, the app forces this user onto /profile
    // before letting them see anything else — see MainLayout's completion gate.
    bool WasDirectlyAdded = false, bool ProfileCompleted = false);

public sealed record UserListItemDto(
    Guid Id, string Mobile, string FullName, string? Email,
    string Role, string Status, DateTime CreatedAt);

public sealed record PagedResult<T>(
    IReadOnlyList<T> Items, int TotalCount, int PageNumber, int PageSize, int TotalPages);

/// <summary>One rated event. Mirrors RatingHistoryItemDto in Application/Ratings/Queries.</summary>
public sealed record RatingHistoryItemDto(
    Guid RatingId, Guid EventId, string EventName, DateTime? EventDate,
    int Performance, int Cooperation, decimal Score, string? Comment,
    string? RaterName, DateTime RatedAt, bool IsLegacySingleScore);

/// <summary>
/// A user's reputation, broken out rather than flattened to one number.
/// Mirrors UserRatingSummaryDto in Application/Ratings/Queries.
/// </summary>
public sealed record UserRatingSummaryDto(
    Guid UserId, string Role, decimal? Average,
    decimal? AveragePerformance, decimal? AverageCooperation,
    int RatedEventCount,
    IReadOnlyDictionary<int, int> Distribution,
    IReadOnlyList<RatingHistoryItemDto> Recent);

public interface IUserApiService
{
    Task<UserProfileDto?> GetMeAsync(CancellationToken ct = default);
    Task<PagedResult<UserListItemDto>?> GetUsersAsync(int page = 1, int pageSize = 20, string? search = null, string? role = null, CancellationToken ct = default);
    Task<bool> ChangeStatusAsync(Guid userId, string status, CancellationToken ct = default);
    Task<bool> UpdateProfileAsync(
        string fullName, string? email, string? avatarUrl, string? inviteMessageTemplate = null,
        DateTime? dateOfBirth = null, string? city = null, string? state = null, string? address = null,
        string? bio = null, string? skills = null, int? experienceYears = null,
        string? businessName = null, string? contactPersonName = null, string? gstNumber = null, string? website = null,
        CancellationToken ct = default);
    Task<(bool Ok, string? Error)> CreateVendorAsync(string mobile, string fullName, string? businessName, string? email, CancellationToken ct = default);
    Task<(bool Ok, string? Error)> CreateCrewAsync(string mobile, string fullName, string? email, string? referralCode, CancellationToken ct = default);

    /// <summary>
    /// Full rating breakdown for one user -- both axes, the star distribution and
    /// recent feedback. Used by RatingSummaryCard on the dashboards and profile.
    /// </summary>
    Task<UserRatingSummaryDto?> GetRatingSummaryAsync(Guid userId, CancellationToken ct = default);
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
        int page = 1, int pageSize = 20, string? search = null, string? role = null, CancellationToken ct = default)
    {
        try
        {
            var url = $"api/v1/users?page={page}&pageSize={pageSize}";
            if (!string.IsNullOrEmpty(search)) url += $"&search={Uri.EscapeDataString(search)}";
            if (!string.IsNullOrEmpty(role))   url += $"&role={Uri.EscapeDataString(role)}";
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

    public async Task<bool> UpdateProfileAsync(
        string fullName, string? email, string? avatarUrl, string? inviteMessageTemplate = null,
        DateTime? dateOfBirth = null, string? city = null, string? state = null, string? address = null,
        string? bio = null, string? skills = null, int? experienceYears = null,
        string? businessName = null, string? contactPersonName = null, string? gstNumber = null, string? website = null,
        CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PutAsJsonAsync("api/v1/users/me", new
            {
                fullName, email, avatarUrl, inviteMessageTemplate,
                dateOfBirth, city, state, address, bio, skills, experienceYears,
                businessName, contactPersonName, gstNumber, website
            }, ct);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

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

    public async Task<UserRatingSummaryDto?> GetRatingSummaryAsync(
        Guid userId, CancellationToken ct = default)
    {
        try
        {
            var r = await _http.GetFromJsonAsync<ApiResult<UserRatingSummaryDto>>(
                $"api/v1/users/{userId}/rating-summary", JsonOpts, ct);
            return r?.Data;
        }
        // Null, not an exception: a dashboard must still render when one card's
        // data is unavailable.
        catch { return null; }
    }
}

/// <summary>
/// Converter that reads both numeric (0,1,2) and string ("Admin","Manager") enum values
/// and stores them as their string name.
/// </summary>
public sealed class FlexibleEnumStringConverter : JsonConverter<string>
{
    public override bool CanConvert(Type typeToConvert) => false;
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetString();
    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        => writer.WriteStringValue(value);
}
