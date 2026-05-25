using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EventWOS.BlazorWeb.Services;

// ── DTOs (mirror Application layer) ──────────────────────────────────────────

public sealed record DashboardStatsDto(
    int TotalEvents,
    int DraftEvents,
    int PublishedEvents,
    int InProgressEvents,
    int CompletedEvents,
    int CancelledEvents,

    int TotalCrew,
    int TotalVendors,
    int TotalAssignments,
    int ConfirmedAssignments,
    int PendingAssignments,
    int DeclinedAssignments,

    int    TotalCheckIns,
    double AttendanceRate,

    IReadOnlyList<RecentActivityDto> RecentActivity,
    IReadOnlyList<UpcomingEventDto>  UpcomingEvents,
    IReadOnlyList<TopVendorDto>      TopVendors
);

public sealed record RecentActivityDto(string Action, string Actor, string Target, DateTime At);

public sealed record UpcomingEventDto(
    Guid   Id,
    string Title,
    string Venue,
    DateTime StartAt,
    int AssignedCrew,
    int MaxCrew,
    string Status
);

public sealed record TopVendorDto(
    string VendorName,
    int    CrewCount,
    int    AssignmentsCount,
    double ConfirmationRate
);

// ── Interface ─────────────────────────────────────────────────────────────────

public interface IAnalyticsApiService
{
    Task<DashboardStatsDto?> GetDashboardStatsAsync(CancellationToken ct = default);
}

// ── Implementation ────────────────────────────────────────────────────────────

public sealed class AnalyticsApiService : IAnalyticsApiService
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public AnalyticsApiService(HttpClient http) => _http = http;

    public async Task<DashboardStatsDto?> GetDashboardStatsAsync(CancellationToken ct = default)
    {
        try
        {
            var r = await _http.GetFromJsonAsync<ApiResult<DashboardStatsDto>>(
                "api/v1/analytics/dashboard", _jsonOpts, ct);
            return r?.Data;
        }
        catch { return null; }
    }
}
