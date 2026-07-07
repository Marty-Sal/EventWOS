using System.Net.Http.Json;
using System.Text.Json;

namespace EventWOS.BlazorWeb.Services;

// ── DTOs (mirror server side) ─────────────────────────────────────────────
public sealed record PendingCheckInDto(
    Guid     Id,
    string   Code,
    DateTimeOffset ExpiresAt,
    string   Status,
    Guid     AssignmentId,
    Guid     EventId,
    string   EventTitle);

public sealed record CheckInVerifyResultDto(
    Guid     AssignmentId,
    Guid     CrewId,
    string   CrewName,
    Guid     EventId,
    string   EventTitle,
    string?  ShiftScopeName,
    DateTimeOffset CheckedInAt);

public interface ICheckInApiService
{
    /// <summary>Crew mints a new QR (auto-cancels any prior live one).
    /// <paramref name="location"/> is the crew's "lat,lng" from their own
    /// device — the server rejects with CheckIn.LocationRequired if
    /// missing/malformed. Callers must have already run the location gate
    /// before calling this.</summary>
    Task<ApiResult<PendingCheckInDto>>      RequestAsync(Guid assignmentId, string? location, CancellationToken ct = default);

    /// <summary>Fetch the caller's currently-live QR for an assignment
    /// (used to rehydrate the modal after a page refresh).</summary>
    Task<ApiResult<PendingCheckInDto>>      GetMyLiveAsync(Guid assignmentId, CancellationToken ct = default);

    /// <summary>Vendor / Manager / Admin verifies a scanned code. The
    /// AttendanceRecord's coords come from the crew's PendingCheckIn
    /// (captured on the crew's own device at request time), not from
    /// the scanning device — so we don't send a vendor location here.</summary>
    Task<ApiResult<CheckInVerifyResultDto>> VerifyAsync(string code, CancellationToken ct = default);
}

public sealed class CheckInApiService : ICheckInApiService
{
    private readonly HttpClient _http;
    // Server serialises DTOs with default (PascalCase) options; the client's
    // ApiResult shape wraps them. Case-insensitive keeps us safe if either
    // side ever adds camelCase policy.
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public CheckInApiService(HttpClient http) => _http = http;

    public async Task<ApiResult<PendingCheckInDto>> RequestAsync(
        Guid assignmentId, string? location, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync(
            "api/v1/attendance/checkin/request",
            new { assignmentId, location }, ct);
        return await ParseAsync<PendingCheckInDto>(resp);
    }

    public async Task<ApiResult<PendingCheckInDto>> GetMyLiveAsync(
        Guid assignmentId, CancellationToken ct = default)
    {
        // 404 is the normal "no live QR yet" signal — we surface it as a
        // failed ApiResult so callers can just check Success and move on
        // without try/catch acrobatics.
        var resp = await _http.GetAsync(
            $"api/v1/attendance/checkin/my/{assignmentId}", ct);
        return await ParseAsync<PendingCheckInDto>(resp);
    }

    public async Task<ApiResult<CheckInVerifyResultDto>> VerifyAsync(
        string code, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync(
            "api/v1/attendance/checkin/verify",
            new { code }, ct);
        return await ParseAsync<CheckInVerifyResultDto>(resp);
    }

    private static async Task<ApiResult<T>> ParseAsync<T>(HttpResponseMessage resp)
    {
        var content = await resp.Content.ReadAsStringAsync();
        try
        {
            var parsed = JsonSerializer.Deserialize<ApiResult<T>>(content, JsonOpts)
                         ?? new ApiResult<T>(false, default, "Unexpected response.", null);

            // The server's ApiResponse envelope carries failure reasons in
            // Errors[], not Message. Normalise so a caller reading .Message
            // sees a useful string ("Event.NotInProgress: …") rather than
            // null — which was making the modal fall back to its generic
            // "Could not generate a check-in QR" for every real error.
            if (!parsed.Success
                && string.IsNullOrWhiteSpace(parsed.Message)
                && parsed.Errors is { Count: > 0 })
            {
                return parsed with { Message = string.Join(" · ", parsed.Errors) };
            }

            return parsed;
        }
        catch
        {
            // Non-JSON error body (e.g. plain 401 from the auth middleware).
            return new ApiResult<T>(false, default,
                resp.IsSuccessStatusCode ? "Unexpected response." : $"HTTP {(int)resp.StatusCode}",
                null);
        }
    }
}
