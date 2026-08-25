using System.Net;

namespace EventOpsOracle.Application.UnitTests.Locations;

/// <summary>
/// Test double for the location provider's HTTP transport.
///
/// Deliberately hand-rolled rather than mocked: HttpMessageHandler's only
/// member is a protected SendAsync, and a small explicit stub reads far better
/// than the reflection gymnastics a mocking library needs to reach it. It also
/// lets a test simulate a hang (via <see cref="Delay"/>) which is how the
/// timeout path is exercised without waiting on a real network.
///
/// No test in this suite ever touches the real Nominatim service — that would
/// make the build depend on a third party's uptime and rate limits.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _status;
    private readonly string         _body;

    /// <summary>Simulates a slow/hung provider.</summary>
    public TimeSpan Delay { get; init; } = TimeSpan.Zero;

    /// <summary>When set, SendAsync throws this instead of responding.</summary>
    public Exception? ThrowOnSend { get; init; }

    /// <summary>Every absolute URL the service requested, in order.</summary>
    public List<string> RequestedUrls { get; } = new();

    public StubHttpMessageHandler(HttpStatusCode status, string body)
    {
        _status = status;
        _body   = body;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestedUrls.Add(request.RequestUri!.ToString());

        if (ThrowOnSend is not null) throw ThrowOnSend;

        if (Delay > TimeSpan.Zero)
            // Honour the token so the service's own timeout can cut this short —
            // that's precisely the behaviour under test.
            await Task.Delay(Delay, cancellationToken);

        return new HttpResponseMessage(_status)
        {
            Content = new StringContent(_body),
        };
    }
}
