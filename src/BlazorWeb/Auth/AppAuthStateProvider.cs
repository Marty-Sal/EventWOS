using Blazored.LocalStorage;
using Microsoft.AspNetCore.Components.Authorization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;

namespace EventWOS.BlazorWeb.Auth;

/// <summary>
/// Blazor WASM authentication state provider.
/// Reads JWT from local storage, parses claims, and exposes auth state.
/// </summary>
public sealed class AppAuthStateProvider : AuthenticationStateProvider
{
    private const string AccessTokenKey  = "ew_access";
    private const string RefreshTokenKey = "ew_refresh";
    private const string SessionIdKey    = "ew_session";

    private readonly ILocalStorageService _storage;
    private readonly HttpClient _http;

    public AppAuthStateProvider(ILocalStorageService storage, HttpClient http)
    {
        _storage = storage;
        _http = http;
    }

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        try
        {
            var token = await _storage.GetItemAsStringAsync(AccessTokenKey);

            if (string.IsNullOrWhiteSpace(token))
                return Anonymous();

            var claims = ParseClaims(token);
            if (!claims.Any())
                return Anonymous();

            // Check token expiry
            var expClaim = claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Exp);
            if (expClaim is not null)
            {
                var exp = DateTimeOffset.FromUnixTimeSeconds(long.Parse(expClaim.Value));
                if (exp < DateTimeOffset.UtcNow)
                    return Anonymous();
            }

            // Inject Bearer token into HTTP client default headers
            _http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            var identity = new ClaimsIdentity(claims, "jwt", JwtRegisteredClaimNames.Sub, ClaimTypes.Role);
            return new AuthenticationState(new ClaimsPrincipal(identity));
        }
        catch
        {
            return Anonymous();
        }
    }

    public async Task MarkLoggedInAsync(string accessToken, string refreshToken, Guid sessionId)
    {
        await _storage.SetItemAsStringAsync(AccessTokenKey, accessToken);
        await _storage.SetItemAsStringAsync(RefreshTokenKey, refreshToken);
        await _storage.SetItemAsStringAsync(SessionIdKey, sessionId.ToString());
        _http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }

    public async Task MarkLoggedOutAsync()
    {
        await _storage.RemoveItemAsync(AccessTokenKey);
        await _storage.RemoveItemAsync(RefreshTokenKey);
        await _storage.RemoveItemAsync(SessionIdKey);
        _http.DefaultRequestHeaders.Authorization = null;
        NotifyAuthenticationStateChanged(Task.FromResult(Anonymous()));
    }

    public async Task<string?> GetRefreshTokenAsync() =>
        await _storage.GetItemAsStringAsync(RefreshTokenKey);

    public async Task<string?> GetAccessTokenAsync() =>
        await _storage.GetItemAsStringAsync(AccessTokenKey);

    public async Task UpdateAccessTokenAsync(string accessToken, string refreshToken)
    {
        await _storage.SetItemAsStringAsync(AccessTokenKey, accessToken);
        await _storage.SetItemAsStringAsync(RefreshTokenKey, refreshToken);
        _http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static AuthenticationState Anonymous() =>
        new(new ClaimsPrincipal(new ClaimsIdentity()));

    private static IEnumerable<Claim> ParseClaims(string jwt)
    {
        var handler = new JwtSecurityTokenHandler();
        if (!handler.CanReadToken(jwt)) return Enumerable.Empty<Claim>();
        var token = handler.ReadJwtToken(jwt);

        // JwtSecurityTokenHandler.ReadJwtToken does NOT remap short claim names to
        // their long-form URIs. We must manually remap "role" → ClaimTypes.Role so
        // that AuthorizeView Roles= checks work correctly in Blazor WASM.
        return token.Claims.Select(c => c.Type switch
        {
            "role"   => new Claim(ClaimTypes.Role,        c.Value),
            "sub"    => new Claim(ClaimTypes.NameIdentifier, c.Value),
            "mobile" => new Claim("mobile",               c.Value),
            _        => c
        });
    }
}
