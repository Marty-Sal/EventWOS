using EventWOS.BlazorWeb.Auth;

namespace EventWOS.BlazorWeb.Services;

/// <summary>
/// Background service that proactively refreshes the access token
/// 5 minutes before it expires, using the stored refresh token.
/// </summary>
public sealed class TokenRefreshService
{
    private readonly AppAuthStateProvider _auth;
    private readonly IAuthApiService _authApi;
    private Timer? _timer;

    public TokenRefreshService(AppAuthStateProvider auth, IAuthApiService authApi)
    {
        _auth = auth;
        _authApi = authApi;
    }

    public void Start()
    {
        // Check every 55 minutes (access tokens live for 60 min)
        _timer = new Timer(async _ => await TryRefreshAsync(), null,
            TimeSpan.FromMinutes(55), TimeSpan.FromMinutes(55));
    }

    public void Stop() => _timer?.Dispose();

    private async Task TryRefreshAsync()
    {
        try
        {
            var refreshToken = await _auth.GetRefreshTokenAsync();
            if (string.IsNullOrEmpty(refreshToken)) return;

            var result = await _authApi.RefreshTokenAsync(refreshToken);
            if (result?.Success == true && result.Data is not null)
            {
                await _auth.UpdateAccessTokenAsync(result.Data.AccessToken, result.Data.RefreshToken);
            }
            else
            {
                // Refresh failed — force logout
                await _auth.MarkLoggedOutAsync();
            }
        }
        catch
        {
            // Swallow — will retry next cycle
        }
    }
}
