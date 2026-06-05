using Blazored.LocalStorage;
using EventWOS.BlazorWeb.Auth;
using Microsoft.AspNetCore.Components;

namespace EventWOS.BlazorWeb.Services;

/// <summary>
/// Global 401 catcher. If ANY API request comes back with 401 Unauthorized,
/// we treat it as "your session has been killed" — clear tokens, flip the
/// auth state to logged-out, and force-navigate to /login?reason=session_revoked.
///
/// This is the primary mechanism. The 30s /sessions/ping heartbeat is just a
/// backstop for idle tabs that aren't making other calls.
///
/// We deliberately swallow the 401 with a synthetic 401 response so callers
/// don't throw HttpRequestException — by the time they would have, we've
/// already navigated away anyway.
/// </summary>
public sealed class UnauthorizedRedirectHandler : DelegatingHandler
{
    private readonly IServiceProvider _sp;
    private static bool _redirecting; // module-wide latch to avoid redirect storms

    public UnauthorizedRedirectHandler(IServiceProvider sp)
    {
        _sp = sp;
        InnerHandler = new HttpClientHandler();
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var response = await base.SendAsync(request, ct);

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && !_redirecting)
        {
            // /api/v1/auth/* endpoints (login, refresh, otp) legitimately return 401
            // before the user is logged in — never bounce on those.
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.Contains("/auth/", StringComparison.OrdinalIgnoreCase))
                return response;

            _redirecting = true;
            try
            {
                var auth = _sp.GetService<AppAuthStateProvider>();
                if (auth is not null)
                    await auth.MarkLoggedOutAsync();

                var nav = _sp.GetService<NavigationManager>();
                nav?.NavigateTo("/login?reason=session_revoked", forceLoad: true);
            }
            catch
            {
                // best-effort — never let cleanup throw on top of an already-bad request
            }
        }

        return response;
    }
}
