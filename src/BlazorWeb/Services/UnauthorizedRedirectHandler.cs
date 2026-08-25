using Blazored.LocalStorage;
using EventOpsOracle.BlazorWeb.Auth;
using Microsoft.AspNetCore.Components;

namespace EventOpsOracle.BlazorWeb.Services;

/// <summary>
/// Global 401 catcher. If ANY API request comes back with 401 Unauthorized,
/// we treat it as "your session has ended" — clear tokens, flip the auth
/// state to logged-out, and force-navigate to /login.
///
/// The API attaches an X-Auth-Fail-Reason header on every 401 with one of:
///   - expired   (JWT lifetime ran out — natural end of session)
///   - revoked   (admin revoked the session in the DB)
///   - inactive  (user account was suspended / deactivated)
/// We forward that as ?reason= on the redirect so Login.razor can render the
/// right copy. If the header is absent we default to 'expired' — the gentler
/// of the two messages.
///
/// This is the primary mechanism. The 30s /sessions/ping heartbeat is just a
/// backstop for idle tabs that aren't making other calls.
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

                var reason = response.Headers.TryGetValues("X-Auth-Fail-Reason", out var vals)
                    ? vals.FirstOrDefault() ?? "expired"
                    : "expired";

                var nav = _sp.GetService<NavigationManager>();
                if (nav is null)
                    return response;

                // ─── INFINITE-RELOAD CIRCUIT BREAKER ─────────────────────────
                // This was a real production outage. NavigateTo(forceLoad: true)
                // to a URL the browser is ALREADY on is just location.reload().
                // So a single 401 raised while sitting on /login produced:
                //     boot → authenticated call 401s → forceLoad /login
                //          → boot → same call 401s → forceLoad /login → ...
                // an endless ~1.5s reload cycle where the login page flashes in
                // and then vanishes, so nobody could ever sign in. A stale token
                // left in localStorage is what kept generating the 401 (the
                // client only reads the local exp claim, so a token revoked or
                // rejected server-side still *looks* authenticated here).
                //
                // Being already on a /login route means the user is exactly
                // where this handler wants to send them. Tokens are cleared
                // above, so the correct action is to STOP — never re-navigate
                // to where we already are.
                var current = nav.ToBaseRelativePath(nav.Uri);
                var currentPath = ("/" + current.Split('?')[0].Split('#')[0])
                    .TrimEnd('/');
                if (currentPath.StartsWith("/login", StringComparison.OrdinalIgnoreCase)
                    || currentPath.StartsWith("/register", StringComparison.OrdinalIgnoreCase)
                    || currentPath.StartsWith("/setup-password", StringComparison.OrdinalIgnoreCase))
                {
                    // Already on a public auth page — nothing to redirect to.
                    // Leave the latch set so a burst of parallel 401s from the
                    // same page load can't queue up a reload either.
                    return response;
                }

                nav.NavigateTo($"/login?reason={Uri.EscapeDataString(reason)}", forceLoad: true);
            }
            catch
            {
                // best-effort — never let cleanup throw on top of an already-bad request
            }
        }

        return response;
    }
}
