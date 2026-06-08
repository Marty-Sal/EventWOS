using System.Net.Http.Json;
using System.Text.Json;

namespace EventWOS.BlazorWeb.Services;

// ── DTOs ──────────────────────────────────────────────────────────────────────

public sealed record CrewGroupDto(
    Guid     Id,
    Guid     VendorId,
    string   Name,
    string?  Description,
    int      MemberCount,
    DateTime CreatedAt);

public sealed record CrewGroupMemberDto(
    Guid     Id,
    Guid     CrewId,
    string   FullName,
    string   Mobile,
    decimal  DisciplineScore,
    int      EventsAttended,
    DateTime AddedAt);

public sealed record CrewGroupDetailDto(
    Guid     Id,
    Guid     VendorId,
    string   Name,
    string?  Description,
    DateTime CreatedAt,
    IReadOnlyList<CrewGroupMemberDto> Members);

public sealed record PagedCrewGroupResult(
    IReadOnlyList<CrewGroupDto> Items,
    int TotalCount,
    int PageNumber,
    int PageSize);

public sealed record VendorAssignGroupResultDto(
    Guid     GroupId,
    string   GroupName,
    int      Invited,
    int      SkippedAlreadyOnEvent,
    int      Failed,
    IReadOnlyList<string> InvitedNames,
    IReadOnlyList<string> SkippedNames,
    IReadOnlyList<VendorAssignGroupFailureDto> Failures);

public sealed record VendorAssignGroupFailureDto(
    Guid    CrewId,
    string  FullName,
    string  Reason);

// ── Interface ─────────────────────────────────────────────────────────────────

public interface ICrewGroupApiService
{
    Task<PagedCrewGroupResult?> GetGroupsAsync(int page = 1, string? search = null, CancellationToken ct = default);
    Task<CrewGroupDetailDto?>    GetGroupAsync(Guid id, CancellationToken ct = default);
    Task<(bool Ok, CrewGroupDto? Group, string? Error)>      CreateAsync(string name, string? description, CancellationToken ct = default);
    Task<(bool Ok, CrewGroupDto? Group, string? Error)>      UpdateAsync(Guid id, string? name, string? description, CancellationToken ct = default);
    Task<(bool Ok, string? Error)>                           DeleteAsync(Guid id, CancellationToken ct = default);
    Task<(bool Ok, CrewGroupDto? Group, string? Error)>      SetMembersAsync(Guid id, IReadOnlyList<Guid> crewIds, CancellationToken ct = default);
    Task<(bool Ok, VendorAssignGroupResultDto? Result, string? Error)> InviteGroupToEventAsync(Guid eventId, Guid groupId, Guid? shiftId = null, CancellationToken ct = default);
}

// ── Implementation ────────────────────────────────────────────────────────────

public sealed class CrewGroupApiService : ICrewGroupApiService
{
    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _jsonOpts = new(JsonSerializerDefaults.Web);
    public CrewGroupApiService(HttpClient http) => _http = http;

    public async Task<PagedCrewGroupResult?> GetGroupsAsync(int page = 1, string? search = null, CancellationToken ct = default)
    {
        try
        {
            var url = $"api/v1/crew-groups?page={page}&pageSize=50";
            if (!string.IsNullOrWhiteSpace(search))
                url += $"&search={Uri.EscapeDataString(search)}";
            var r = await _http.GetFromJsonAsync<ApiResult<PagedCrewGroupResult>>(url, _jsonOpts, ct);
            return r?.Data;
        }
        catch { return null; }
    }

    public async Task<CrewGroupDetailDto?> GetGroupAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            var r = await _http.GetFromJsonAsync<ApiResult<CrewGroupDetailDto>>(
                $"api/v1/crew-groups/{id}", _jsonOpts, ct);
            return r?.Data;
        }
        catch { return null; }
    }

    public async Task<(bool Ok, CrewGroupDto? Group, string? Error)> CreateAsync(
        string name, string? description, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync("api/v1/crew-groups",
                new { name, description }, ct);
            var body = await TryReadAsync<CrewGroupDto>(resp, ct);
            if (resp.IsSuccessStatusCode) return (true, body?.Data, null);
            return (false, null, body?.Errors?.FirstOrDefault() ?? body?.Message ?? "Failed to create group.");
        }
        catch (Exception ex) { return (false, null, ex.Message); }
    }

    public async Task<(bool Ok, CrewGroupDto? Group, string? Error)> UpdateAsync(
        Guid id, string? name, string? description, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PatchAsJsonAsync($"api/v1/crew-groups/{id}",
                new { name, description }, ct);
            var body = await TryReadAsync<CrewGroupDto>(resp, ct);
            if (resp.IsSuccessStatusCode) return (true, body?.Data, null);
            return (false, null, body?.Errors?.FirstOrDefault() ?? body?.Message ?? "Failed to update group.");
        }
        catch (Exception ex) { return (false, null, ex.Message); }
    }

    public async Task<(bool Ok, string? Error)> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.DeleteAsync($"api/v1/crew-groups/{id}", ct);
            if (resp.IsSuccessStatusCode) return (true, null);
            var body = await TryReadAsync<object>(resp, ct);
            return (false, body?.Errors?.FirstOrDefault() ?? body?.Message ?? "Failed to delete group.");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public async Task<(bool Ok, CrewGroupDto? Group, string? Error)> SetMembersAsync(
        Guid id, IReadOnlyList<Guid> crewIds, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync($"api/v1/crew-groups/{id}/members",
                new { crewIds }, ct);
            var body = await TryReadAsync<CrewGroupDto>(resp, ct);
            if (resp.IsSuccessStatusCode) return (true, body?.Data, null);
            return (false, null, body?.Errors?.FirstOrDefault() ?? body?.Message ?? "Failed to update members.");
        }
        catch (Exception ex) { return (false, null, ex.Message); }
    }

    public async Task<(bool Ok, VendorAssignGroupResultDto? Result, string? Error)> InviteGroupToEventAsync(
        Guid eventId, Guid groupId, Guid? shiftId = null, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync(
                $"api/v1/events/{eventId}/vendor-assign-group",
                new { groupId, shiftId }, ct);
            var body = await TryReadAsync<VendorAssignGroupResultDto>(resp, ct);
            if (resp.IsSuccessStatusCode) return (true, body?.Data, null);
            return (false, null, body?.Errors?.FirstOrDefault() ?? body?.Message ?? "Failed to invite group.");
        }
        catch (Exception ex) { return (false, null, ex.Message); }
    }

    private async Task<ApiResult<T>?> TryReadAsync<T>(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            if (resp.Content.Headers.ContentLength == 0) return null;
            return await resp.Content.ReadFromJsonAsync<ApiResult<T>>(_jsonOpts, ct);
        }
        catch { return null; }
    }
}
