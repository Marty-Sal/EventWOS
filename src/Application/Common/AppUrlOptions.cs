namespace EventWOS.Application.Common;

/// <summary>
/// Public-facing URL of the Blazor frontend. Used by approval emails / SMS
/// to embed the right login link, and by anything else server-side that
/// needs to construct a link a user will click in their browser.
///
/// Configured via appsettings.json ("AppUrls" section) or env var
/// <c>AppUrls__BaseUrl</c> (Railway / Docker convention). Falls back to
/// the legacy <c>APP_BASE_URL</c> env var so we don't break existing deploys
/// during the transition window.
///
/// Pattern matches existing <c>JwtOptions</c> / <c>OtpOptions</c>:
/// typed options class colocated with the layer that consumes it,
/// SectionName constant, registered with Configure&lt;T&gt; in Program.cs.
/// </summary>
public sealed class AppUrlOptions
{
    public const string SectionName = "AppUrls";

    /// <summary>
    /// Base URL of the Blazor frontend, no trailing slash required
    /// (handlers should TrimEnd defensively). Example: "https://eventwos.app".
    /// </summary>
    public string BaseUrl { get; init; } = "https://eventwos.app";
}
