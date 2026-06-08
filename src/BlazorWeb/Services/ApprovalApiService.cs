using System.Net.Http.Json;
using System.Text.Json;

namespace EventWOS.BlazorWeb.Services;

public sealed record PendingRegistrationDto(
    Guid    UserId,
    string  Username,
    string  Email,
    string  Mobile,
    string  FullName,
    string  Role,
    DateTime RegisteredAt,
    string?  BusinessName,
    string?  ContactPersonName,
    string?  City,
    string?  Website,
    string?  Skills,
    int?     ExperienceYears,
    string?  ReferralCodeUsed,
    Guid?    ReferredVendorId,
    string?  ReferredVendorName);

public sealed record ApprovalQueueDto(
    int VendorCount,
    int CrewCount,
    IReadOnlyList<PendingRegistrationDto> Vendors,
    IReadOnlyList<PendingRegistrationDto> Crew);

public sealed record ApproveResultDto(Guid UserId, string Role, string? ReferralCode);

public interface IApprovalApiService
{
    Task<ApiResult<ApprovalQueueDto>> GetQueueAsync(CancellationToken ct = default);
    Task<ApiResult<ApproveResultDto>> ApproveAsync(Guid userId, CancellationToken ct = default);
    Task<ApiResult<object>>           RejectAsync(Guid userId, string reason, CancellationToken ct = default);
}

public sealed class ApprovalApiService : IApprovalApiService
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    public ApprovalApiService(HttpClient http) => _http = http;

    public async Task<ApiResult<ApprovalQueueDto>> GetQueueAsync(CancellationToken ct = default)
    {
        var resp = await _http.GetAsync("api/v1/admin/approval-queue", ct);
        return await ParseAsync<ApprovalQueueDto>(resp);
    }

    public async Task<ApiResult<ApproveResultDto>> ApproveAsync(Guid userId, CancellationToken ct = default)
    {
        var resp = await _http.PostAsync($"api/v1/admin/approval-queue/{userId}/approve", content: null, ct);
        return await ParseAsync<ApproveResultDto>(resp);
    }

    public async Task<ApiResult<object>> RejectAsync(Guid userId, string reason, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync($"api/v1/admin/approval-queue/{userId}/reject", new { reason }, ct);
        return await ParseAsync<object>(resp);
    }

    private static async Task<ApiResult<T>> ParseAsync<T>(HttpResponseMessage resp)
    {
        var content = await resp.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<ApiResult<T>>(content, JsonOpts)
               ?? new ApiResult<T>(false, default, "Unexpected response.", null);
    }
}
