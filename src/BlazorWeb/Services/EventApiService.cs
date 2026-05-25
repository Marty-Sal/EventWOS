using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EventWOS.BlazorWeb.Services;

// ── DTOs ──────────────────────────────────────────────────────────────────────
public sealed record EventListItemDto(
    Guid Id, string Title, string Venue,
    DateTime StartAt, DateTime EndAt,
    string Status, int MaxCrew, int AssignedCrew, DateTime CreatedAt);

public sealed record EventDetailDto(
    Guid Id, string Title, string? Description, string Venue, string? Address,
    DateTime StartAt, DateTime EndAt, string Status, int MaxCrew, int AssignedCrew,
    Guid CreatedByUserId, string CreatedByName, DateTime CreatedAt);

public sealed record EventAssignmentDto(
    Guid Id, Guid EventId, string EventTitle,
    Guid CrewId, string CrewName, string CrewMobile,
    Guid VendorId, string VendorName,
    string Status, DateTime? ConfirmedAt, DateTime? DeclinedAt, DateTime CreatedAt);

public sealed record AttendanceRecordDto(
    Guid Id, Guid AssignmentId, Guid EventId, Guid CrewId,
    string CrewName, string Action, DateTime RecordedAt, string? Location);

public sealed record AttendanceSummaryDto(
    Guid EventId, string EventTitle,
    int TotalAssigned, int TotalConfirmed, int TotalAttended, int TotalNoShow,
    IReadOnlyList<CrewAttendanceDto> CrewDetails);

public sealed record CrewAttendanceDto(
    Guid CrewId, string CrewName, string AssignmentStatus,
    DateTime? CheckInAt, DateTime? CheckOutAt);

public sealed record PagedEventResult(
    IReadOnlyList<EventListItemDto> Items, int TotalCount, int Page, int PageSize);

public sealed record PagedEventAssignmentResult(
    IReadOnlyList<EventAssignmentDto> Items, int TotalCount, int Page, int PageSize);

// ── Interface ─────────────────────────────────────────────────────────────────
public interface IEventApiService
{
    // Admin / Manager
    Task<PagedEventResult?> GetEventsAsync(int page = 1, string? search = null, string? status = null, CancellationToken ct = default);
    Task<EventDetailDto?> GetEventAsync(Guid id, CancellationToken ct = default);
    Task<(bool Ok, string? Error, EventDetailDto? Data)> CreateEventAsync(CreateEventRequest req, CancellationToken ct = default);
    Task<(bool Ok, string? Error)> UpdateEventAsync(Guid id, CreateEventRequest req, CancellationToken ct = default);
    Task<(bool Ok, string? Error)> ChangeEventStatusAsync(Guid id, string action, string? reason = null, CancellationToken ct = default);
    Task<PagedEventAssignmentResult?> GetAssignmentsAsync(Guid eventId, int page = 1, string? status = null, CancellationToken ct = default);
    Task<(bool Ok, string? Error)> AssignCrewAsync(Guid eventId, Guid crewId, Guid vendorId, CancellationToken ct = default);
    Task<AttendanceSummaryDto?> GetAttendanceSummaryAsync(Guid eventId, CancellationToken ct = default);
    Task<(bool Ok, string? Error)> RecordAttendanceAsync(Guid assignmentId, string action, string? location = null, CancellationToken ct = default);

    // Crew / Vendor — my own assignments
    Task<PagedEventAssignmentResult?> GetMyAssignmentsAsync(int page = 1, CancellationToken ct = default);
    Task<PagedEventAssignmentResult?> GetVendorAssignmentsAsync(int page = 1, CancellationToken ct = default);
    Task<(bool Ok, string? Error)> RespondAssignmentAsync(Guid assignmentId, string response, string? reason = null, CancellationToken ct = default);
}

public sealed record CreateEventRequest(
    string Title, string? Description, string Venue, string? Address,
    DateTime StartAt, DateTime EndAt, int MaxCrew);

// ── Implementation ────────────────────────────────────────────────────────────
public sealed class EventApiService : IEventApiService
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public EventApiService(HttpClient http) => _http = http;

    public async Task<PagedEventResult?> GetEventsAsync(int page = 1, string? search = null, string? status = null, CancellationToken ct = default)
    {
        try
        {
            var url = $"api/v1/events?page={page}&pageSize=20";
            if (search is not null) url += $"&search={Uri.EscapeDataString(search)}";
            if (status is not null) url += $"&status={Uri.EscapeDataString(status)}";
            var r = await _http.GetFromJsonAsync<ApiResult<PagedEventResult>>(url, _jsonOpts, ct);
            return r?.Data;
        }
        catch { return null; }
    }

    public async Task<EventDetailDto?> GetEventAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            var r = await _http.GetFromJsonAsync<ApiResult<EventDetailDto>>($"api/v1/events/{id}", _jsonOpts, ct);
            return r?.Data;
        }
        catch { return null; }
    }

    public async Task<(bool Ok, string? Error, EventDetailDto? Data)> CreateEventAsync(CreateEventRequest req, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync("api/v1/events", req, ct);
            var body = await resp.Content.ReadFromJsonAsync<ApiResult<EventDetailDto>>(_jsonOpts, ct);
            return resp.IsSuccessStatusCode
                ? (true, null, body?.Data)
                : (false, body?.Errors?.FirstOrDefault() ?? "Unknown error", null);
        }
        catch (Exception ex) { return (false, ex.Message, null); }
    }

    public async Task<(bool Ok, string? Error)> UpdateEventAsync(Guid id, CreateEventRequest req, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PutAsJsonAsync($"api/v1/events/{id}", req, ct);
            if (resp.IsSuccessStatusCode) return (true, null);
            var body = await resp.Content.ReadFromJsonAsync<ApiResult<object>>(_jsonOpts, ct);
            return (false, body?.Errors?.FirstOrDefault() ?? "Unknown error");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public async Task<(bool Ok, string? Error)> ChangeEventStatusAsync(Guid id, string action, string? reason = null, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PatchAsJsonAsync($"api/v1/events/{id}/status", new { action, reason }, ct);
            if (resp.IsSuccessStatusCode) return (true, null);
            var body = await resp.Content.ReadFromJsonAsync<ApiResult<object>>(_jsonOpts, ct);
            return (false, body?.Errors?.FirstOrDefault() ?? "Unknown error");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public async Task<PagedEventAssignmentResult?> GetAssignmentsAsync(Guid eventId, int page = 1, string? status = null, CancellationToken ct = default)
    {
        try
        {
            var url = $"api/v1/events/{eventId}/assignments?page={page}&pageSize=50";
            if (status is not null) url += $"&status={Uri.EscapeDataString(status)}";
            var r = await _http.GetFromJsonAsync<ApiResult<PagedEventAssignmentResult>>(url, _jsonOpts, ct);
            return r?.Data;
        }
        catch { return null; }
    }

    public async Task<(bool Ok, string? Error)> AssignCrewAsync(Guid eventId, Guid crewId, Guid vendorId, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync($"api/v1/events/{eventId}/assignments", new { crewId, vendorId }, ct);
            if (resp.IsSuccessStatusCode) return (true, null);
            var body = await resp.Content.ReadFromJsonAsync<ApiResult<object>>(_jsonOpts, ct);
            return (false, body?.Errors?.FirstOrDefault() ?? "Unknown error");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public async Task<AttendanceSummaryDto?> GetAttendanceSummaryAsync(Guid eventId, CancellationToken ct = default)
    {
        try
        {
            var r = await _http.GetFromJsonAsync<ApiResult<AttendanceSummaryDto>>($"api/v1/events/{eventId}/attendance", _jsonOpts, ct);
            return r?.Data;
        }
        catch { return null; }
    }

    public async Task<(bool Ok, string? Error)> RecordAttendanceAsync(Guid assignmentId, string action, string? location = null, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync($"api/v1/events/assignments/{assignmentId}/attendance", new { action, location }, ct);
            if (resp.IsSuccessStatusCode) return (true, null);
            var body = await resp.Content.ReadFromJsonAsync<ApiResult<object>>(_jsonOpts, ct);
            return (false, body?.Errors?.FirstOrDefault() ?? "Unknown error");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    // ── Crew / Vendor ─────────────────────────────────────────────────────────

    public async Task<PagedEventAssignmentResult?> GetMyAssignmentsAsync(int page = 1, CancellationToken ct = default)
    {
        try
        {
            var r = await _http.GetFromJsonAsync<ApiResult<PagedEventAssignmentResult>>(
                $"api/v1/events/my-assignments?page={page}&pageSize=20", _jsonOpts, ct);
            return r?.Data;
        }
        catch { return null; }
    }

    public async Task<PagedEventAssignmentResult?> GetVendorAssignmentsAsync(int page = 1, CancellationToken ct = default)
    {
        try
        {
            var r = await _http.GetFromJsonAsync<ApiResult<PagedEventAssignmentResult>>(
                $"api/v1/events/vendor-assignments?page={page}&pageSize=20", _jsonOpts, ct);
            return r?.Data;
        }
        catch { return null; }
    }

    public async Task<(bool Ok, string? Error)> RespondAssignmentAsync(Guid assignmentId, string response, string? reason = null, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PatchAsJsonAsync(
                $"api/v1/events/assignments/{assignmentId}/respond",
                new { response, reason }, ct);
            if (resp.IsSuccessStatusCode) return (true, null);
            var body = await resp.Content.ReadFromJsonAsync<ApiResult<object>>(_jsonOpts, ct);
            return (false, body?.Errors?.FirstOrDefault() ?? "Unknown error");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }
}
