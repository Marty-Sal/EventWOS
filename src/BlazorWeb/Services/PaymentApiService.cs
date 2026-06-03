using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EventWOS.BlazorWeb.Services;

// ── Form models (used by Payments.razor) ─────────────────────────────────────

public sealed class NewPaymentForm
{
    public Guid     EventId      { get; set; }
    public Guid     AssignmentId { get; set; }
    public Guid     CrewId       { get; set; }
    public Guid     VendorId     { get; set; }
    public decimal  AgreedAmount { get; set; }
    public string?  Notes        { get; set; }
}

public sealed class NewBatchForm
{
    public Guid         VendorId             { get; set; }
    public Guid         EventId              { get; set; }
    public List<Guid>   PaymentIds           { get; set; } = new();
    public string?      Notes                { get; set; }
    /// <summary>
    /// When set and PaymentIds is empty, the server will auto-create payments
    /// at this rate for every attended crew member that does not yet have one,
    /// then fold them into the new batch.
    /// </summary>
    public decimal?     DefaultAmountPerCrew { get; set; }
}



// ── Event-centric batch builder ───────────────────────────────────────────────

public sealed record EventPayableRosterDto(
    Guid    EventId,
    string  EventTitle,
    string  EventStatus,
    DateTime EventStartAt,
    IReadOnlyList<PayableLineDto> VendorLines,
    IReadOnlyList<PayableLineDto> DirectCrewLines
);

public sealed record PayableLineDto(
    string  Kind,
    Guid    PartyId,
    string  PartyName,
    string  PartyMobile,
    int     AttendedCrewCount,
    decimal SuggestedAmount,
    bool    AlreadyHasPayment
);

public sealed class EventBatchLineForm
{
    public string  Kind    { get; set; } = "Vendor";
    public Guid    PartyId { get; set; }
    public decimal Amount  { get; set; }
}

public sealed record EventBatchResult(
    int             BatchesCreated,
    int             PaymentsCreated,
    decimal         TotalAmount,
    IReadOnlyList<Guid> BatchIds
);

// ── DTOs ──────────────────────────────────────────────────────────────────────

public sealed record CrewPaymentDto(
    Guid     Id,
    Guid     EventId,
    string   EventTitle,
    Guid     AssignmentId,
    Guid     CrewId,
    string   CrewName,
    string   CrewMobile,
    Guid?    VendorId,
    string?  VendorName,
    decimal  AgreedAmount,
    decimal? PaidAmount,
    string   Status,
    string?  Method,
    string?  TransactionRef,
    string?  Notes,
    DateTime? PaidAt,
    Guid?    PayrollBatchId,
    string   CrewAcknowledgment,
    DateTime? AcknowledgedAt,
    string?  AcknowledgmentNote,
    string?  BatchStatus,
    DateTime CreatedDate
);

public sealed record PayrollBatchDto(
    Guid     Id,
    Guid?    VendorId,
    string?  VendorName,
    Guid     EventId,
    string   EventTitle,
    string   BatchRef,
    string   Status,
    decimal  TotalAmount,
    string?  Notes,
    int      PaymentCount,
    DateTime? SubmittedAt,
    DateTime? ApprovedAt,
    DateTime? DisbursedAt,
    DateTime CreatedDate
);

public sealed record PagedPaymentResult(
    IReadOnlyList<CrewPaymentDto> Items,
    int TotalCount,
    int PageNumber,
    int PageSize
);

public sealed record PagedPayrollResult(
    IReadOnlyList<PayrollBatchDto> Items,
    int TotalCount,
    int PageNumber,
    int PageSize
);

// ── Interface ─────────────────────────────────────────────────────────────────

public interface IPaymentApiService
{
    Task<PagedPaymentResult?> GetPaymentsAsync(
        Guid? eventId = null, Guid? vendorId = null, Guid? crewId = null,
        string? status = null, int page = 1, int pageSize = 20,
        CancellationToken ct = default);

    Task<(bool Ok, string? Error)> CreatePaymentAsync(
        NewPaymentForm form, CancellationToken ct = default);

    Task<(bool Ok, string? Error)> UpdatePaymentStatusAsync(
        Guid paymentId, string action, decimal? paidAmount = null,
        string? method = null, string? transactionRef = null,
        string? reason = null, CancellationToken ct = default);

    Task<PagedPayrollResult?> GetPayrollBatchesAsync(
        Guid? vendorId = null, Guid? eventId = null,
        string? status = null, int page = 1,
        CancellationToken ct = default);

    Task<(bool Ok, string? Error)> CreatePayrollBatchAsync(
        NewBatchForm form, CancellationToken ct = default);

    Task<(EventPayableRosterDto? Roster, string? Error)> GetEventPayableRosterAsync(
        Guid eventId, CancellationToken ct = default);

    Task<(bool Ok, string? Error)> CreateEventPayrollBatchAsync(
        Guid eventId, List<EventBatchLineForm> lines, string? notes,
        CancellationToken ct = default);

    Task<(bool Ok, string? Error)> UpdatePayrollBatchStatusAsync(
        Guid batchId, string action, string? reason = null,
        CancellationToken ct = default);
}

// ── Implementation ────────────────────────────────────────────────────────────

public sealed class PaymentApiService : IPaymentApiService
{
    private readonly HttpClient _http;

    /// <summary>
    /// Safely extracts a human-readable error from a non-success HTTP response.
    /// Handles 401/403 (permission/auth) with friendly messages, and tolerates
    /// empty or non-JSON error bodies that would otherwise crash JSON parsing.
    /// </summary>
    private async Task<string> ExtractErrorAsync(
        HttpResponseMessage r, string fallback, CancellationToken ct)
    {
        // Permission / auth errors get a friendly message regardless of body
        if (r.StatusCode == System.Net.HttpStatusCode.Forbidden)
            return "You do not have permission to perform this action. Please contact your administrator.";
        if (r.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            return "Your session has expired. Please sign in again.";

        // Try to parse a structured ApiResult, but degrade gracefully
        try
        {
            var raw = await r.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(raw)) return fallback;

            // Body might be plain text (e.g. ASP.NET default 500 page)
            if (!raw.TrimStart().StartsWith("{") && !raw.TrimStart().StartsWith("["))
                return raw.Length > 200 ? raw[..200] : raw;

            var err = System.Text.Json.JsonSerializer
                .Deserialize<ApiResult<object>>(raw, _jsonOpts);
            // ApiResponse<T>.Fail puts the error message in Errors[0]; ApiResponse<T>.Ok
            // uses Message for an optional success note. Prefer the first error we can find.
            var firstErr = err?.Errors is { Count: > 0 } ? err.Errors[0] : null;
            return firstErr ?? err?.Message ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public PaymentApiService(HttpClient http) => _http = http;

    public async Task<PagedPaymentResult?> GetPaymentsAsync(
        Guid? eventId = null, Guid? vendorId = null, Guid? crewId = null,
        string? status = null, int page = 1, int pageSize = 20,
        CancellationToken ct = default)
    {
        try
        {
            var url = $"api/v1/payments?page={page}&pageSize={pageSize}";
            if (eventId.HasValue)  url += $"&eventId={eventId}";
            if (vendorId.HasValue) url += $"&vendorId={vendorId}";
            if (crewId.HasValue)   url += $"&crewId={crewId}";
            if (status != null)    url += $"&status={Uri.EscapeDataString(status)}";
            var r = await _http.GetFromJsonAsync<ApiResult<PagedPaymentResult>>(url, _jsonOpts, ct);
            return r?.Data;
        }
        catch { return null; }
    }

    public async Task<(bool Ok, string? Error)> CreatePaymentAsync(
        NewPaymentForm form, CancellationToken ct = default)
    {
        try
        {
            var body = new
            {
                eventId      = form.EventId,
                assignmentId = form.AssignmentId,
                crewId       = form.CrewId,
                vendorId     = form.VendorId,
                agreedAmount = form.AgreedAmount,
                notes        = form.Notes
            };
            var r = await _http.PostAsJsonAsync("api/v1/payments", body, ct);
            if (r.IsSuccessStatusCode) return (true, null);
            return (false, await ExtractErrorAsync(r, "Failed to create payment.", ct));
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public async Task<(bool Ok, string? Error)> UpdatePaymentStatusAsync(
        Guid paymentId, string action, decimal? paidAmount = null,
        string? method = null, string? transactionRef = null,
        string? reason = null, CancellationToken ct = default)
    {
        try
        {
            var body = new { action, paidAmount, method, transactionRef, reason };
            var r = await _http.PatchAsJsonAsync($"api/v1/payments/{paymentId}/status", body, ct);
            if (r.IsSuccessStatusCode) return (true, null);
            return (false, await ExtractErrorAsync(r, "Status update failed.", ct));
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public async Task<PagedPayrollResult?> GetPayrollBatchesAsync(
        Guid? vendorId = null, Guid? eventId = null,
        string? status = null, int page = 1,
        CancellationToken ct = default)
    {
        try
        {
            var url = $"api/v1/payments/payroll?page={page}&pageSize=20";
            if (vendorId.HasValue) url += $"&vendorId={vendorId}";
            if (eventId.HasValue)  url += $"&eventId={eventId}";
            if (status != null)    url += $"&status={Uri.EscapeDataString(status)}";
            var r = await _http.GetFromJsonAsync<ApiResult<PagedPayrollResult>>(url, _jsonOpts, ct);
            return r?.Data;
        }
        catch { return null; }
    }

    public async Task<(bool Ok, string? Error)> CreatePayrollBatchAsync(
        NewBatchForm form, CancellationToken ct = default)
    {
        try
        {
            var body = new
            {
                vendorId             = form.VendorId,
                eventId              = form.EventId,
                paymentIds           = form.PaymentIds,
                notes                = form.Notes,
                defaultAmountPerCrew = form.DefaultAmountPerCrew
            };
            var r = await _http.PostAsJsonAsync("api/v1/payments/payroll", body, ct);
            if (r.IsSuccessStatusCode) return (true, null);
            return (false, await ExtractErrorAsync(r, "Failed to create payroll batch.", ct));
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public async Task<(bool Ok, string? Error)> UpdatePayrollBatchStatusAsync(
        Guid batchId, string action, string? reason = null,
        CancellationToken ct = default)
    {
        try
        {
            var body = new { action, reason };
            var r = await _http.PatchAsJsonAsync($"api/v1/payments/payroll/{batchId}/status", body, ct);
            if (r.IsSuccessStatusCode) return (true, null);
            return (false, await ExtractErrorAsync(r, "Status update failed.", ct));
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public async Task<(EventPayableRosterDto? Roster, string? Error)> GetEventPayableRosterAsync(
        Guid eventId, CancellationToken ct = default)
    {
        try
        {
            var r = await _http.GetAsync($"api/v1/payments/event/{eventId}/payable-roster", ct);
            if (!r.IsSuccessStatusCode)
                return (null, await ExtractErrorAsync(r, "Failed to load payable roster.", ct));
            var env = await r.Content.ReadFromJsonAsync<ApiResult<EventPayableRosterDto>>(_jsonOpts, ct);
            return (env?.Data, null);
        }
        catch (Exception ex) { return (null, ex.Message); }
    }

    public async Task<(bool Ok, string? Error)> CreateEventPayrollBatchAsync(
        Guid eventId, List<EventBatchLineForm> lines, string? notes,
        CancellationToken ct = default)
    {
        try
        {
            var body = new
            {
                lines = lines.Select(l => new { kind = l.Kind, partyId = l.PartyId, amount = l.Amount }),
                notes
            };
            var r = await _http.PostAsJsonAsync($"api/v1/payments/event/{eventId}/payroll", body, ct);
            if (r.IsSuccessStatusCode) return (true, null);
            return (false, await ExtractErrorAsync(r, "Failed to create payroll batch.", ct));
        }
        catch (Exception ex) { return (false, ex.Message); }
    }
}
