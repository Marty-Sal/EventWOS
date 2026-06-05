using Blazored.LocalStorage;
using EventWOS.BlazorWeb.Auth;
using Microsoft.AspNetCore.Components;

namespace EventWOS.BlazorWeb.Services;

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
                nav?.NavigateTo($"/login?reason={Uri.EscapeDataString(reason)}", forceLoad: true);
            }
            catch
            {
                // best-effort — never let cleanup throw on top of an already-bad request
            }
        }

        return response;
    }
}
