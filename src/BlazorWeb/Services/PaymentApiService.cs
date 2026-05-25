using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EventWOS.BlazorWeb.Services;

// ── DTOs ──────────────────────────────────────────────────────────────────────

public sealed record CrewPaymentDto(
    Guid     Id,
    Guid     EventId,
    string   EventTitle,
    Guid     AssignmentId,
    Guid     CrewId,
    string   CrewName,
    string   CrewMobile,
    Guid     VendorId,
    string   VendorName,
    decimal  AgreedAmount,
    decimal? PaidAmount,
    string   Status,
    string?  Method,
    string?  TransactionRef,
    string?  Notes,
    DateTime? PaidAt,
    Guid?    PayrollBatchId,
    DateTime CreatedDate
);

public sealed record PayrollBatchDto(
    Guid     Id,
    Guid     VendorId,
    string   VendorName,
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
    Task<PagedPaymentResult?> GetPaymentsAsync(Guid? eventId = null, Guid? vendorId = null,
        string? status = null, int page = 1, CancellationToken ct = default);

    Task<(bool Ok, string? Error)> CreatePaymentAsync(
        Guid eventId, Guid assignmentId, Guid crewId, Guid vendorId,
        decimal agreedAmount, string? notes, CancellationToken ct = default);

    Task<(bool Ok, string? Error)> UpdatePaymentStatusAsync(
        Guid paymentId, string action, decimal? paidAmount = null,
        string? method = null, string? transactionRef = null,
        string? reason = null, CancellationToken ct = default);

    Task<PagedPayrollResult?> GetPayrollBatchesAsync(
        Guid? vendorId = null, Guid? eventId = null,
        string? status = null, int page = 1, CancellationToken ct = default);

    Task<(bool Ok, Guid? BatchId, string? Error)> CreatePayrollBatchAsync(
        Guid vendorId, Guid eventId, string? notes,
        IReadOnlyList<Guid> paymentIds, CancellationToken ct = default);

    Task<(bool Ok, string? Error)> UpdatePayrollStatusAsync(
        Guid batchId, string action, string? reason = null, CancellationToken ct = default);
}

// ── Implementation ────────────────────────────────────────────────────────────

public sealed class PaymentApiService : IPaymentApiService
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public PaymentApiService(HttpClient http) => _http = http;

    public async Task<PagedPaymentResult?> GetPaymentsAsync(
        Guid? eventId = null, Guid? vendorId = null,
        string? status = null, int page = 1, CancellationToken ct = default)
    {
        try
        {
            var url = $"api/v1/payments?page={page}&pageSize=20";
            if (eventId.HasValue)  url += $"&eventId={eventId}";
            if (vendorId.HasValue) url += $"&vendorId={vendorId}";
            if (status != null)    url += $"&status={status}";
            var r = await _http.GetFromJsonAsync<ApiResult<PagedPaymentResult>>(url, _jsonOpts, ct);
            return r?.Data;
        }
        catch { return null; }
    }

    public async Task<(bool Ok, string? Error)> CreatePaymentAsync(
        Guid eventId, Guid assignmentId, Guid crewId, Guid vendorId,
        decimal agreedAmount, string? notes, CancellationToken ct = default)
    {
        try
        {
            var body = new { eventId, assignmentId, crewId, vendorId, agreedAmount, notes };
            var r = await _http.PostAsJsonAsync("api/v1/payments", body, ct);
            if (r.IsSuccessStatusCode) return (true, null);
            var err = await r.Content.ReadFromJsonAsync<ApiResult<object>>(_jsonOpts, ct);
            return (false, err?.Message ?? "Failed to create payment.");
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
            var err = await r.Content.ReadFromJsonAsync<ApiResult<object>>(_jsonOpts, ct);
            return (false, err?.Message ?? "Status update failed.");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public async Task<PagedPayrollResult?> GetPayrollBatchesAsync(
        Guid? vendorId = null, Guid? eventId = null,
        string? status = null, int page = 1, CancellationToken ct = default)
    {
        try
        {
            var url = $"api/v1/payments/payroll?page={page}&pageSize=20";
            if (vendorId.HasValue) url += $"&vendorId={vendorId}";
            if (eventId.HasValue)  url += $"&eventId={eventId}";
            if (status != null)    url += $"&status={status}";
            var r = await _http.GetFromJsonAsync<ApiResult<PagedPayrollResult>>(url, _jsonOpts, ct);
            return r?.Data;
        }
        catch { return null; }
    }

    public async Task<(bool Ok, Guid? BatchId, string? Error)> CreatePayrollBatchAsync(
        Guid vendorId, Guid eventId, string? notes,
        IReadOnlyList<Guid> paymentIds, CancellationToken ct = default)
    {
        try
        {
            var body = new { vendorId, eventId, notes, paymentIds };
            var r = await _http.PostAsJsonAsync("api/v1/payments/payroll", body, ct);
            if (r.IsSuccessStatusCode)
            {
                var res = await r.Content.ReadFromJsonAsync<ApiResult<Guid>>(_jsonOpts, ct);
                return (true, res?.Data, null);
            }
            var err = await r.Content.ReadFromJsonAsync<ApiResult<object>>(_jsonOpts, ct);
            return (false, null, err?.Message ?? "Failed to create payroll batch.");
        }
        catch (Exception ex) { return (false, null, ex.Message); }
    }

    public async Task<(bool Ok, string? Error)> UpdatePayrollStatusAsync(
        Guid batchId, string action, string? reason = null, CancellationToken ct = default)
    {
        try
        {
            var body = new { action, reason };
            var r = await _http.PatchAsJsonAsync($"api/v1/payments/payroll/{batchId}/status", body, ct);
            if (r.IsSuccessStatusCode) return (true, null);
            var err = await r.Content.ReadFromJsonAsync<ApiResult<object>>(_jsonOpts, ct);
            return (false, err?.Message ?? "Status update failed.");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }
}
