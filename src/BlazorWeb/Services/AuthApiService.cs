using EventWOS.BlazorWeb.Auth;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EventWOS.BlazorWeb.Services;

// ─── Request/Response models ──────────────────────────────────────────────────
public sealed record RequestOtpRequest(string Mobile);
public sealed record VerifyOtpRequest(string Mobile, string Otp, Guid OtpRequestId, string DeviceId);
public sealed record RefreshRequest(string RefreshToken);
public sealed record LogoutRequest(string RefreshToken);

public sealed record OtpInitiatedDto(Guid OtpRequestId, string Mobile, int ExpiryMinutes, string Message, string? DevOtp = null);
public sealed record AuthResultDto(
    string AccessToken, string RefreshToken,
    DateTime AccessTokenExpiry, DateTime RefreshTokenExpiry,
    Guid SessionId, UserInfoDto User);
public sealed record UserInfoDto(
    Guid Id, string Mobile, string FullName, string Role,
    IReadOnlyList<string> Permissions);

public sealed record ApiResult<T>(bool Success, T? Data, string? Message, IReadOnlyList<string>? Errors);

public interface IAuthApiService
{
    Task<ApiResult<OtpInitiatedDto>> RequestOtpAsync(string mobile, CancellationToken ct = default);
    Task<ApiResult<AuthResultDto>> VerifyOtpAsync(string mobile, string otp, Guid requestId, string deviceId, CancellationToken ct = default);
    Task LogoutAsync(string refreshToken, CancellationToken ct = default);
    Task<ApiResult<AuthResultDto>?> RefreshTokenAsync(string refreshToken, CancellationToken ct = default);
}

public sealed class AuthApiService : IAuthApiService
{
    private readonly HttpClient _http;
    private readonly AppAuthStateProvider _authState;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public AuthApiService(HttpClient http, AppAuthStateProvider authState)
    {
        _http = http;
        _authState = authState;
    }

    public async Task<ApiResult<OtpInitiatedDto>> RequestOtpAsync(string mobile, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync("api/v1/auth/request-otp", new RequestOtpRequest(mobile), ct);
        return await ParseAsync<OtpInitiatedDto>(resp);
    }

    public async Task<ApiResult<AuthResultDto>> VerifyOtpAsync(
        string mobile, string otp, Guid requestId, string deviceId, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync("api/v1/auth/verify-otp",
            new VerifyOtpRequest(mobile, otp, requestId, deviceId), ct);
        var result = await ParseAsync<AuthResultDto>(resp);

        if (result.Success && result.Data is not null)
        {
            await _authState.MarkLoggedInAsync(
                result.Data.AccessToken,
                result.Data.RefreshToken,
                result.Data.SessionId);
        }
        return result;
    }

    public async Task LogoutAsync(string refreshToken, CancellationToken ct = default)
    {
        await _http.PostAsJsonAsync("api/v1/auth/logout", new LogoutRequest(refreshToken), ct);
        await _authState.MarkLoggedOutAsync();
    }

    public async Task<ApiResult<AuthResultDto>?> RefreshTokenAsync(string refreshToken, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync("api/v1/auth/refresh", new RefreshRequest(refreshToken), ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await ParseAsync<AuthResultDto>(resp);
    }

    private static async Task<ApiResult<T>> ParseAsync<T>(HttpResponseMessage resp)
    {
        var content = await resp.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<ApiResult<T>>(content, JsonOpts)
               ?? new ApiResult<T>(false, default, "Unexpected response.", null);
    }
}
