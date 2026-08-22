using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EventWOS.BlazorWeb.Services;

// ── DTOs ──────────────────────────────────────────────────────────────────────
public sealed record VendorListItemDto(
    Guid Id, string Mobile, string FullName, string? BusinessName,
    string Status, string? ReferralCode, decimal? Rating, int RatingCount,
    int EventsCompleted, int CrewCount, DateTime CreatedAt);

public sealed record VendorDetailDto(
    Guid Id, string Mobile, string FullName, string? BusinessName, string? Email,
    string? AvatarUrl, string Status, string? ReferralCode, decimal? Rating, int RatingCount,
    int EventsCompleted, int CrewCount, DateTime CreatedAt,
    string? ContactPersonName = null, string? GstNumber = null, string? Address = null,
    string? City = null, string? State = null, string? Website = null, string? Bio = null,
    DateTime? DateOfBirth = null, IReadOnlyList<FileDocumentDto>? Files = null,
    bool WasDirectlyAdded = false, bool ProfileCompleted = false);

public sealed record CrewMemberDto(
    Guid Id, string Mobile, string FullName, string? Email, string? AvatarUrl,
    string Status, Guid? VendorId, string? VendorName,
    decimal DisciplineScore, int EventsAttended, DateTime CreatedAt,
    // Null average = not yet rated. Rendered as "Not rated", never zero stars.
    decimal? CrewRating = null, int CrewRatingCount = 0);

/// <summary>Full profile for the Crew page's "View details" modal — see CrewDetailDto (server-side) in Application/Vendors/DTOs/VendorDto.cs.</summary>
public sealed record CrewDetailDto(
    Guid Id, string Mobile, string FullName, string? Email, string? AvatarUrl,
    string Status, Guid? VendorId, string? VendorName,
    decimal DisciplineScore, int EventsAttended, DateTime CreatedAt,
    string? City, string? State, string? Bio, string? Skills, int? ExperienceYears,
    string? ReferralCodeUsed, DateTime? DateOfBirth, IReadOnlyList<FileDocumentDto> Files,
    bool WasDirectlyAdded = false, bool ProfileCompleted = false,
    decimal? CrewRating = null, int CrewRatingCount = 0);

/// <summary>
/// A completed event a vendor worked, plus the rating already given for it.
/// Mirrors RateableEventDto in Application/Ratings/Queries.
/// </summary>
public sealed record RateableEventDto(
    Guid EventId, string EventTitle, string Venue, DateTime StartAt,
    bool AlreadyRated, int? Performance, int? Cooperation,
    string? Comment, DateTime? RatedAt);

public sealed record PagedVendorResult(
    IReadOnlyList<VendorListItemDto> Items, int TotalCount, int Page, int PageSize);

public sealed record PagedCrewResult(
    IReadOnlyList<CrewMemberDto> Items, int TotalCount, int Page, int PageSize);

// ── Interface ─────────────────────────────────────────────────────────────────

// ── Vendor Report DTOs ────────────────────────────────────────────────────────

public sealed record VendorReportDto(
    Guid    VendorId,
    string  VendorName,
    int     TotalCrewInRoster,
    int     TotalAssignmentsMade,
    int     AssignmentsConfirmed,
    int     AssignmentsAttended,
    int     AssignmentsPending,
    int     AssignmentsRejected,
    decimal ConfirmationRate,
    decimal AttendanceRate,
    decimal TotalAgreedAmount,
    decimal TotalPaidAmount,
    decimal TotalPendingAmount,
    int     TotalEventsWorked,
    IReadOnlyList<VendorCrewStatDto>? TopCrew);

public sealed record VendorCrewStatDto(
    Guid    CrewId,
    string  CrewName,
    string  CrewMobile,
    decimal DisciplineScore,
    int     EventsAttended,
    int     AssignmentsForThisVendor,
    string  LastStatus);

public interface IVendorApiService
{
    Task<PagedVendorResult?> GetVendorsAsync(int page = 1, string? search = null, int pageSize = 20, string? status = null, CancellationToken ct = default);
    Task<VendorDetailDto?> GetVendorAsync(Guid id, CancellationToken ct = default);
    Task<(bool Ok, string? Error)> CreateVendorAsync(string mobile, string fullName, string? businessName, string? email, CancellationToken ct = default);
    /// <summary>Completed events this vendor worked, for the rating event picker.</summary>
    Task<IReadOnlyList<RateableEventDto>> GetRateableEventsAsync(Guid vendorId, CancellationToken ct = default);

    /// <summary>
    /// Rates a vendor on ONE completed event. Returns an error message, or null on
    /// success. Event-scoped because a vendor's reputation is the average across
    /// the events they worked -- the previous global call overwrote the single
    /// stored score, so each new rating silently erased the last one.
    /// </summary>
    Task<string?> RateVendorAsync(Guid vendorId, Guid eventId, int performance,
                                  int cooperation, string? comment, CancellationToken ct = default);
    Task<bool> ChangeVendorStatusAsync(Guid id, string status, CancellationToken ct = default);
    Task<bool> ChangeCrewStatusAsync(Guid id, string status, CancellationToken ct = default);
    Task<PagedCrewResult?> GetCrewAsync(int page = 1, string? search = null, Guid? vendorId = null, int pageSize = 20, string? status = null, CancellationToken ct = default);
    Task<CrewDetailDto?> GetCrewDetailAsync(Guid id, CancellationToken ct = default);
    Task<(bool Ok, string? Error)> CreateCrewAsync(string mobile, string fullName, string? email, string? referralCode, CancellationToken ct = default);
    Task<VendorReportDto?> GetMyReportAsync(CancellationToken ct = default);
}

// ── Implementation ────────────────────────────────────────────────────────────
public sealed class VendorApiService : IVendorApiService
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public VendorApiService(HttpClient http) => _http = http;

    public async Task<PagedVendorResult?> GetVendorsAsync(int page = 1, string? search = null, int pageSize = 20, string? status = null, CancellationToken ct = default)
    {
        try
        {
            var url = $"api/v1/vendors?page={page}&pageSize={pageSize}";
            if (search != null) url += $"&search={Uri.EscapeDataString(search)}";
            if (!string.IsNullOrWhiteSpace(status)) url += $"&status={Uri.EscapeDataString(status)}";
            var r = await _http.GetFromJsonAsync<ApiResult<PagedVendorResult>>(url, _jsonOpts, ct);
            return r?.Data;
        }
        catch { return null; }
    }

    public async Task<VendorDetailDto?> GetVendorAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            var r = await _http.GetFromJsonAsync<ApiResult<VendorDetailDto>>($"api/v1/vendors/{id}", _jsonOpts, ct);
            return r?.Data;
        }
        catch { return null; }
    }

    public async Task<CrewDetailDto?> GetCrewDetailAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            var r = await _http.GetFromJsonAsync<ApiResult<CrewDetailDto>>($"api/v1/crew/{id}", _jsonOpts, ct);
            return r?.Data;
        }
        catch { return null; }
    }

    public async Task<(bool Ok, string? Error)> CreateVendorAsync(string mobile, string fullName, string? businessName, string? email, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync("api/v1/vendors",
                new { mobile, fullName, businessName, email }, ct);
            if (resp.IsSuccessStatusCode) return (true, null);
            var body = await resp.Content.ReadFromJsonAsync<ApiResult<object>>(_jsonOpts, ct);
            return (false, body?.Errors?.FirstOrDefault() ?? "Failed to create vendor.");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public async Task<IReadOnlyList<RateableEventDto>> GetRateableEventsAsync(
        Guid vendorId, CancellationToken ct = default)
    {
        try
        {
            var r = await _http.GetFromJsonAsync<ApiResult<List<RateableEventDto>>>(
                $"api/v1/vendors/{vendorId}/rateable-events", _jsonOpts, ct);
            return r?.Data ?? new List<RateableEventDto>();
        }
        // Empty rather than throwing: the dialog then reports "nothing to rate"
        // instead of leaving a half-loaded picker on screen.
        catch { return new List<RateableEventDto>(); }
    }

    public async Task<string?> RateVendorAsync(
        Guid vendorId, Guid eventId, int performance, int cooperation,
        string? comment, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync(
                $"api/v1/vendors/{vendorId}/events/{eventId}/rating",
                new { performance, cooperation, comment }, ct);

            if (resp.IsSuccessStatusCode) return null;

            // Surface the server's reason -- 'event not completed' or 'vendor not on
            // this event' are actionable, where a bare failure is not.
            try
            {
                var body = await resp.Content.ReadFromJsonAsync<ApiResult<object>>(_jsonOpts, ct);
                return body?.Errors?.FirstOrDefault() ?? body?.Message
                       ?? $"{(int)resp.StatusCode} {resp.StatusCode}";
            }
            catch { return $"{(int)resp.StatusCode} {resp.StatusCode}"; }
        }
        catch (Exception ex) { return ex.Message; }
    }

    public async Task<bool> ChangeVendorStatusAsync(Guid id, string status, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PatchAsJsonAsync($"api/v1/vendors/{id}/status", new { status }, ct);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<bool> ChangeCrewStatusAsync(Guid id, string status, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PatchAsJsonAsync($"api/v1/crew/{id}/status", new { status }, ct);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<PagedCrewResult?> GetCrewAsync(int page = 1, string? search = null, Guid? vendorId = null, int pageSize = 20, string? status = null, CancellationToken ct = default)
    {
        try
        {
            var url = $"api/v1/crew?page={page}&pageSize={pageSize}";
            if (search != null)   url += $"&search={Uri.EscapeDataString(search)}";
            if (vendorId != null) url += $"&vendorId={vendorId}";
            if (!string.IsNullOrWhiteSpace(status)) url += $"&status={Uri.EscapeDataString(status)}";
            var r = await _http.GetFromJsonAsync<ApiResult<PagedCrewResult>>(url, _jsonOpts, ct);
            return r?.Data;
        }
        catch { return null; }
    }

    public async Task<(bool Ok, string? Error)> CreateCrewAsync(string mobile, string fullName, string? email, string? referralCode, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync("api/v1/crew",
                new { mobile, fullName, email, referralCode }, ct);
            if (resp.IsSuccessStatusCode) return (true, null);
            var body = await resp.Content.ReadFromJsonAsync<ApiResult<object>>(_jsonOpts, ct);
            return (false, body?.Errors?.FirstOrDefault() ?? "Failed to create crew member.");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public async Task<VendorReportDto?> GetMyReportAsync(CancellationToken ct = default)
    {
        try
        {
            var r = await _http.GetFromJsonAsync<ApiResult<VendorReportDto>>(
                "api/v1/vendors/my/report", _jsonOpts, ct);
            return r?.Data;
        }
        catch { return null; }
    }
}
