using EventWOS.BlazorWeb.Auth;
using Microsoft.AspNetCore.Components;

namespace EventWOS.BlazorWeb.Services;

/// <summary>
/// Two responsibilities, both timer-driven:
/// 1. Proactively refresh the access token before expiry.
/// 2. Heartbeat the API every ~30s — if the API rejects the token (401), the
///    session was revoked server-side (admin pressed "Revoke" or the user was
///    suspended) and we immediately force-logout the user in the browser.
/// </summary>
public sealed class TokenRefreshService
{
    private readonly AppAuthStateProvider _auth;
    private readonly IAuthApiService _authApi;
    private readonly HttpClient _http;
    private readonly NavigationManager _nav;
    private Timer? _refreshTimer;
    private Timer? _heartbeatTimer;

    public TokenRefreshService(
        AppAuthStateProvider auth,
        IAuthApiService authApi,
        HttpClient http,
        NavigationManager nav)
    {
        _auth = auth;
        _authApi = authApi;
        _http = http;
        _nav = nav;
    }

    public void Start()
    {
        // Token refresh — every 55 min (access tokens live for 60 min)
        _refreshTimer = new Timer(async _ => await TryRefreshAsync(), null,
            TimeSpan.FromMinutes(55), TimeSpan.FromMinutes(55));

        // Heartbeat — every 30 seconds, lightweight ping to detect server-side revocation
        _heartbeatTimer = new Timer(async _ => await HeartbeatAsync(), null,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public void Stop()
    {
        _refreshTimer?.Dispose();
        _heartbeatTimer?.Dispose();
    }

    private async Task TryRefreshAsync()
    {
        try
        {
            var refreshToken = await _auth.GetRefreshTokenAsync();
            if (string.IsNullOrEmpty(refreshToken)) return;

            var (result, reason) = await _authApi.RefreshTokenWithReasonAsync(refreshToken);
            if (result?.Success == true && result.Data is not null)
            {
                await _auth.UpdateAccessTokenAsync(result.Data.AccessToken, result.Data.RefreshToken);
            }
            else
            {
                // No reason from server → fall back to 'expired' (gentlest copy)
                await ForceLogoutAsync(reason ?? "expired");
            }
        }
        catch
        {
            // Swallow — will retry next cycle
        }
    }

    private async Task HeartbeatAsync()
    {
        try
        {
            // Skip if we don't have a token to begin with — no-op for anonymous users
            var token = await _auth.GetAccessTokenAsync();
            if (string.IsNullOrEmpty(token)) return;

            // Lightweight: GET /api/v1/sessions/me/ping — any 401 = revoked → logout
            using var req = new HttpRequestMessage(HttpMethod.Get, "api/v1/sessions/ping");
            using var resp = await _http.SendAsync(req);

            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                var reason = resp.Headers.TryGetValues("X-Auth-Fail-Reason", out var vals)
                    ? vals.FirstOrDefault() ?? "expired" : "expired";
                await ForceLogoutAsync(reason);
            }
        }
        catch
        {
            // Network blip — ignore, next heartbeat will catch it
        }
    }

    private async Task ForceLogoutAsync(string reason = "expired")
    {
        await _auth.MarkLoggedOutAsync();
        _nav.NavigateTo($"/login?reason={Uri.EscapeDataString(reason)}", forceLoad: true);
    }
}
