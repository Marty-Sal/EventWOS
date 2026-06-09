using System.Net.Http.Json;
using System.Text.Json;

namespace EventWOS.BlazorWeb.Services;

/// <summary>
/// Phase C step 5 — typed client for the VendorAllocations REST controller.
/// Kept separate from EventApiService so the (already-large) event service
/// doesn't accrete every adjacent feature. Same JSON conventions:
/// camelCase, ApiResult&lt;T&gt; envelope, friendly first-error extraction.
/// </summary>
public sealed record VendorAllocationDto(
    Guid     Id,
    Guid     ShiftId,
    Guid     VendorId,
    string   VendorName,
    Guid     EventId,
    Guid     ScopeOfWorkId,
    string   ScopeName,
    int      Quota,
    int      CurrentlyAssigned,
    bool     IsArchived,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    // Phase D step 18: latest placeholder-row Status for this vendor on
    // this shift. Lets the Vendor Quotas panel render an "Accepted" /
    // "Pending Invite" / "Rejected" badge per row without re-fetching
    // assignments. Null when no placeholder row exists.
    string?  InviteStatus = null);

public interface IVendorAllocationApiService
{
    Task<IReadOnlyList<VendorAllocationDto>?> GetForShiftAsync(Guid shiftId, CancellationToken ct = default);
    Task<(bool Ok, string? Error, VendorAllocationDto? Data)> CreateAsync(Guid shiftId, Guid vendorId, int quota, CancellationToken ct = default);
    Task<(bool Ok, string? Error, VendorAllocationDto? Data)> UpdateAsync(Guid allocationId, int quota, CancellationToken ct = default);
    Task<(bool Ok, string? Error)> ArchiveAsync(Guid allocationId, CancellationToken ct = default);
}

public sealed class VendorAllocationApiService : IVendorAllocationApiService
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public VendorAllocationApiService(HttpClient http) => _http = http;

    public async Task<IReadOnlyList<VendorAllocationDto>?> GetForShiftAsync(Guid shiftId, CancellationToken ct = default)
    {
        try
        {
            var r = await _http.GetFromJsonAsync<ApiResult<IReadOnlyList<VendorAllocationDto>>>(
                $"api/v1/event-shifts/{shiftId}/vendor-allocations", _jsonOpts, ct);
            return r?.Data;
        }
        catch { return null; }
    }

    public async Task<(bool Ok, string? Error, VendorAllocationDto? Data)> CreateAsync(
        Guid shiftId, Guid vendorId, int quota, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync(
                $"api/v1/event-shifts/{shiftId}/vendor-allocations",
                new { vendorId, quota }, ct);
            if (resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadFromJsonAsync<ApiResult<VendorAllocationDto>>(_jsonOpts, ct);
                return (true, null, body?.Data);
            }
            var err = await ParseError(resp, ct);
            return (false, err, null);
        }
        catch (Exception ex) { return (false, ex.Message, null); }
    }

    public async Task<(bool Ok, string? Error, VendorAllocationDto? Data)> UpdateAsync(
        Guid allocationId, int quota, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PutAsJsonAsync(
                $"api/v1/vendor-allocations/{allocationId}",
                new { quota }, ct);
            if (resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadFromJsonAsync<ApiResult<VendorAllocationDto>>(_jsonOpts, ct);
                return (true, null, body?.Data);
            }
            var err = await ParseError(resp, ct);
            return (false, err, null);
        }
        catch (Exception ex) { return (false, ex.Message, null); }
    }

    public async Task<(bool Ok, string? Error)> ArchiveAsync(Guid allocationId, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.DeleteAsync($"api/v1/vendor-allocations/{allocationId}", ct);
            if (resp.IsSuccessStatusCode) return (true, null);
            var err = await ParseError(resp, ct);
            return (false, err);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    private static async Task<string?> ParseError(HttpResponseMessage resp, CancellationToken ct)
    {
        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(raw))
            return resp.StatusCode == System.Net.HttpStatusCode.Forbidden
                ? "You do not have permission to perform this action."
                : $"Request failed ({(int)resp.StatusCode}).";
        try
        {
            var body = JsonSerializer.Deserialize<ApiResult<object>>(raw, _jsonOpts);
            return body?.Errors?.FirstOrDefault() ?? "Unknown error";
        }
        catch { return raw; }
    }
}
