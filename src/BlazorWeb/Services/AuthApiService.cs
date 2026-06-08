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

// ─── Phase 3-5: password login / registration / reset ──────────────────────
public sealed record LoginRequest(string UsernameOrEmail, string Password, string Portal, string? DeviceId, string? DeviceName);
public sealed record LoginResultDto(bool RequiresPasswordSetup, string? Mobile, AuthResultDto? Auth);

public sealed record RegisterVendorRequest(
    string Username, string Email, string Mobile, string Password, string FullName,
    string BusinessName, string? ContactPersonName, string? GstNumber,
    string? Address, string? City, string? State, string? Website, string? Bio);
public sealed record RegisterCrewRequest(
    string Username, string Email, string Mobile, string Password, string FullName,
    string? ReferralCode, string? City, string? Skills, int? ExperienceYears, string? Bio);
public sealed record RegistrationResultDto(Guid UserId, string Status, string Message);

public sealed record ForgotPasswordRequest(string UsernameEmailOrMobile);
public sealed record ForgotPasswordResultDto(Guid? OtpRequestId, string MaskedDestination);
public sealed record ResetPasswordRequest(Guid OtpRequestId, string Mobile, string Otp, string NewPassword);
public sealed record SetupPasswordRequest(Guid OtpRequestId, string Mobile, string Otp, string NewUsername, string NewPassword);

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
    /// <summary>
    /// Like <see cref="RefreshTokenAsync(string, CancellationToken)"/> but also
    /// returns the X-Auth-Fail-Reason header (or null) so the caller can show
    /// the right copy on the login page after a force-logout.
    /// </summary>
    Task<(ApiResult<AuthResultDto>? Result, string? FailReason)> RefreshTokenWithReasonAsync(string refreshToken, CancellationToken ct = default);

    // Phase 3: password auth + registration + reset flows.
    Task<ApiResult<LoginResultDto>> LoginAsync(string usernameOrEmail, string password, string portal, string deviceId, string deviceName, CancellationToken ct = default);
    Task<ApiResult<RegistrationResultDto>> RegisterVendorAsync(RegisterVendorRequest req, CancellationToken ct = default);
    Task<ApiResult<RegistrationResultDto>> RegisterCrewAsync(RegisterCrewRequest req, CancellationToken ct = default);
    Task<ApiResult<ForgotPasswordResultDto>> RequestPasswordResetAsync(string usernameEmailOrMobile, CancellationToken ct = default);
    Task<ApiResult<object>> ResetPasswordAsync(ResetPasswordRequest req, CancellationToken ct = default);
    Task<ApiResult<object>> SetupPasswordAsync(SetupPasswordRequest req, CancellationToken ct = default);
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

    public async Task<(ApiResult<AuthResultDto>? Result, string? FailReason)> RefreshTokenWithReasonAsync(
        string refreshToken, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync("api/v1/auth/refresh", new RefreshRequest(refreshToken), ct);
        string? reason = resp.Headers.TryGetValues("X-Auth-Fail-Reason", out var vals)
            ? vals.FirstOrDefault() : null;
        if (!resp.IsSuccessStatusCode) return (null, reason);
        return (await ParseAsync<AuthResultDto>(resp), reason);
    }

    // ─── Password login (Phase 3) ─────────────────────────────────────────
    public async Task<ApiResult<LoginResultDto>> LoginAsync(
        string usernameOrEmail, string password, string portal,
        string deviceId, string deviceName, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync("api/v1/auth/login",
            new LoginRequest(usernameOrEmail, password, portal, deviceId, deviceName), ct);
        var result = await ParseAsync<LoginResultDto>(resp);

        // Successful + has token? Mark logged in. Setup-required = leave alone,
        // caller routes user to /setup-password.
        if (result.Success && result.Data?.Auth is { } auth)
        {
            await _authState.MarkLoggedInAsync(auth.AccessToken, auth.RefreshToken, auth.SessionId);
        }
        return result;
    }

    public async Task<ApiResult<RegistrationResultDto>> RegisterVendorAsync(
        RegisterVendorRequest req, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync("api/v1/auth/register/vendor", req, ct);
        return await ParseAsync<RegistrationResultDto>(resp);
    }

    public async Task<ApiResult<RegistrationResultDto>> RegisterCrewAsync(
        RegisterCrewRequest req, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync("api/v1/auth/register/crew", req, ct);
        return await ParseAsync<RegistrationResultDto>(resp);
    }

    public async Task<ApiResult<ForgotPasswordResultDto>> RequestPasswordResetAsync(
        string usernameEmailOrMobile, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync("api/v1/auth/forgot-password/request",
            new ForgotPasswordRequest(usernameEmailOrMobile), ct);
        return await ParseAsync<ForgotPasswordResultDto>(resp);
    }

    public async Task<ApiResult<object>> ResetPasswordAsync(
        ResetPasswordRequest req, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync("api/v1/auth/forgot-password/reset", req, ct);
        return await ParseAsync<object>(resp);
    }

    public async Task<ApiResult<object>> SetupPasswordAsync(
        SetupPasswordRequest req, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync("api/v1/auth/setup-password", req, ct);
        return await ParseAsync<object>(resp);
    }

    private static async Task<ApiResult<T>> ParseAsync<T>(HttpResponseMessage resp)
    {
        var content = await resp.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<ApiResult<T>>(content, JsonOpts)
               ?? new ApiResult<T>(false, default, "Unexpected response.", null);
    }
}
