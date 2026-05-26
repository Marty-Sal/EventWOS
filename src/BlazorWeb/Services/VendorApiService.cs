using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EventWOS.BlazorWeb.Services;

// ── DTOs ──────────────────────────────────────────────────────────────────────
public sealed record VendorListItemDto(
    Guid Id, string Mobile, string FullName, string? BusinessName,
    string Status, string? ReferralCode, decimal? Rating,
    int EventsCompleted, int CrewCount, DateTime CreatedAt);

public sealed record VendorDetailDto(
    Guid Id, string Mobile, string FullName, string? BusinessName, string? Email,
    string? AvatarUrl, string Status, string? ReferralCode, decimal? Rating,
    int EventsCompleted, int CrewCount, DateTime CreatedAt);

public sealed record CrewMemberDto(
    Guid Id, string Mobile, string FullName, string? Email, string? AvatarUrl,
    string Status, Guid? VendorId, string? VendorName,
    decimal DisciplineScore, int EventsAttended, DateTime CreatedAt);

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
    Task<PagedVendorResult?> GetVendorsAsync(int page = 1, string? search = null, CancellationToken ct = default);
    Task<VendorDetailDto?> GetVendorAsync(Guid id, CancellationToken ct = default);
    Task<(bool Ok, string? Error)> CreateVendorAsync(string mobile, string fullName, string? businessName, string? email, CancellationToken ct = default);
    Task<bool> RateVendorAsync(Guid id, decimal rating, CancellationToken ct = default);
    Task<bool> ChangeVendorStatusAsync(Guid id, string status, CancellationToken ct = default);
    Task<PagedCrewResult?> GetCrewAsync(int page = 1, string? search = null, Guid? vendorId = null, CancellationToken ct = default);
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

    public async Task<PagedVendorResult?> GetVendorsAsync(int page = 1, string? search = null, CancellationToken ct = default)
    {
        try
        {
            var url = $"api/v1/vendors?page={page}&pageSize=20";
            if (search != null) url += $"&search={Uri.EscapeDataString(search)}";
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

    public async Task<bool> RateVendorAsync(Guid id, decimal rating, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PatchAsJsonAsync($"api/v1/vendors/{id}/rating", new { rating }, ct);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
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

    public async Task<PagedCrewResult?> GetCrewAsync(int page = 1, string? search = null, Guid? vendorId = null, CancellationToken ct = default)
    {
        try
        {
            var url = $"api/v1/crew?page={page}&pageSize=20";
            if (search != null)   url += $"&search={Uri.EscapeDataString(search)}";
            if (vendorId != null) url += $"&vendorId={vendorId}";
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
