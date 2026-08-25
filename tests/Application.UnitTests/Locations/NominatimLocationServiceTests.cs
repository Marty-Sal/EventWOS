using System.Net;
using EventOpsOracle.Infrastructure.Locations;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EventOpsOracle.Application.UnitTests.Locations;

/// <summary>
/// Covers the location-provider contract: search success, empty results,
/// provider failure/timeout, and reverse geocoding.
///
/// The contract these tests defend is that provider trouble NEVER surfaces as
/// an exception to the business layer — a geocoding hiccup must not break the
/// venue screen or block a save. Only genuine caller cancellation propagates.
/// </summary>
public sealed class NominatimLocationServiceTests
{
    private static NominatimLocationService Build(
        StubHttpMessageHandler handler, LocationOptions? options = null)
    {
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://nominatim.test/"),
        };

        return new NominatimLocationService(
            http,
            new MemoryCache(new MemoryCacheOptions()),
            Options.Create(options ?? new LocationOptions
            {
                MinQueryLength = 3,
                TimeoutSeconds = 5,
                CacheMinutes   = 0, // off by default so each test hits the stub
            }),
            NullLogger<NominatimLocationService>.Instance);
    }

    private const string TwoResultSearchJson = """
    [
      {
        "place_id": 12345,
        "name": "Millennium Business Park",
        "display_name": "Millennium Business Park, Mahape, Navi Mumbai, Thane, Maharashtra, 400710, India",
        "lat": "19.1052800",
        "lon": "73.0198900",
        "address": {
          "suburb": "Mahape",
          "city": "Navi Mumbai",
          "state": "Maharashtra",
          "postcode": "400710",
          "country": "India"
        }
      },
      {
        "place_id": "67890",
        "name": "DOME, SVP Stadium",
        "display_name": "DOME, SVP Stadium, Mumbai, Maharashtra, India",
        "lat": "18.98656",
        "lon": "72.81547",
        "address": { "city": "Mumbai", "state": "Maharashtra", "country": "India" }
      }
    ]
    """;

    // ── 1. Location search success ───────────────────────────────────────────

    [Fact]
    public async Task SearchAsync_maps_provider_results_to_neutral_dtos()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, TwoResultSearchJson);
        var sut     = Build(handler);

        var results = await sut.SearchAsync("millennium business park", CancellationToken.None);

        results.Should().HaveCount(2);

        var first = results[0];
        first.PlaceId.Should().Be("12345");
        first.Name.Should().Be("Millennium Business Park");
        first.Latitude.Should().Be(19.10528m);
        first.Longitude.Should().Be(73.01989m);
        first.FullAddress.Should().Contain("Navi Mumbai");

        // ShortAddress must be the compact assembled label, NOT display_name —
        // the whole reason it exists is that display_name is unusable in a
        // table row.
        first.ShortAddress.Should().Be("Mahape, Navi Mumbai, Maharashtra");
        first.ShortAddress.Should().NotContain("India");
    }

    [Fact]
    public async Task SearchAsync_returns_the_structured_address_components()
    {
        // Regression: these were parsed to build ShortAddress and then thrown
        // away, so picking a suggestion filled the name and coordinates but left
        // City/State/PostalCode/Country blank on the venue form. The provider
        // hands them to us in the SAME response — dropping them forced the admin
        // to retype the address or nudge the pin to trigger a reverse-geocode for
        // data we already had.
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, TwoResultSearchJson);

        var results = await Build(handler).SearchAsync("millennium business park", CancellationToken.None);

        var first = results[0];
        first.City.Should().Be("Navi Mumbai");
        first.State.Should().Be("Maharashtra");
        first.PostalCode.Should().Be("400710");
        first.Country.Should().Be("India");
    }

    [Fact]
    public async Task SearchAsync_leaves_missing_components_null_rather_than_guessing()
    {
        // The second fixture has no suburb/postcode. A blank field the admin can
        // fill beats a fabricated one they will not notice is wrong.
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, TwoResultSearchJson);

        var second = (await Build(handler).SearchAsync("dome svp", CancellationToken.None))[1];

        second.City.Should().Be("Mumbai");
        second.PostalCode.Should().BeNull();
    }

    [Fact]
    public async Task SearchAsync_survives_a_result_with_no_address_block_at_all()
    {
        // Nominatim omits "address" entirely for some results. The component
        // readers must no-op rather than throw, or one odd result kills the whole
        // suggestion list.
        const string noAddressJson = """
        [ { "place_id": 1, "name": "Somewhere", "display_name": "Somewhere",
            "lat": "19.1", "lon": "73.0" } ]
        """;
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, noAddressJson);

        var results = await Build(handler).SearchAsync("somewhere", CancellationToken.None);

        results.Should().HaveCount(1);
        results[0].City.Should().BeNull();
        results[0].State.Should().BeNull();
        results[0].Country.Should().BeNull();
    }

    [Fact]
    public async Task SearchAsync_reads_place_id_whether_number_or_string()
    {
        // Nominatim has shipped place_id as both a JSON number and a string
        // across versions; neither should drop a result.
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, TwoResultSearchJson);
        var results = await Build(handler).SearchAsync("mumbai venues", CancellationToken.None);

        results.Select(r => r.PlaceId).Should().BeEquivalentTo(new[] { "12345", "67890" });
    }

    // ── 2. Empty search results ──────────────────────────────────────────────

    [Fact]
    public async Task SearchAsync_returns_empty_when_provider_has_no_matches()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, "[]");

        var results = await Build(handler).SearchAsync("zzzzz nonexistent place", CancellationToken.None);

        results.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("mu")]
    public async Task SearchAsync_short_or_blank_query_never_calls_provider(string query)
    {
        // "Still typing" is not an error and must not burn rate-limit budget.
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, TwoResultSearchJson);

        var results = await Build(handler).SearchAsync(query, CancellationToken.None);

        results.Should().BeEmpty();
        handler.RequestedUrls.Should().BeEmpty("queries below MinQueryLength are answered locally");
    }

    // ── 3. Provider failure / timeout ────────────────────────────────────────

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]   // OSM rate limit
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task SearchAsync_returns_empty_and_does_not_throw_on_provider_error(HttpStatusCode status)
    {
        var handler = new StubHttpMessageHandler(status, "upstream exploded");

        var results = await Build(handler).SearchAsync("millennium", CancellationToken.None);

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_returns_empty_when_provider_is_unreachable()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, "[]")
        {
            ThrowOnSend = new HttpRequestException("no such host"),
        };

        var results = await Build(handler).SearchAsync("millennium", CancellationToken.None);

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_returns_empty_when_provider_times_out()
    {
        // Provider hangs for 5 s; our configured budget is 1 s.
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, TwoResultSearchJson)
        {
            Delay = TimeSpan.FromSeconds(5),
        };
        var sut = Build(handler, new LocationOptions
        {
            MinQueryLength = 3,
            TimeoutSeconds = 1,
            CacheMinutes   = 0,
        });

        var results = await sut.SearchAsync("millennium", CancellationToken.None);

        // Degraded, not thrown: a hung provider must not 500 the admin screen.
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_returns_empty_on_malformed_provider_payload()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, "{ this is not json");

        var results = await Build(handler).SearchAsync("millennium", CancellationToken.None);

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_propagates_caller_cancellation()
    {
        // The one case that MUST throw rather than degrade: the debounced search
        // box cancels superseded requests and needs to tell "abandoned" apart
        // from "no results", so it can leave the old suggestions on screen.
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, TwoResultSearchJson)
        {
            Delay = TimeSpan.FromSeconds(5),
        };
        using var cts = new CancellationTokenSource();
        var task = Build(handler).SearchAsync("millennium", cts.Token);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    // ── 4. Reverse geocoding ─────────────────────────────────────────────────

    private const string ReverseJson = """
    {
      "place_id": 555,
      "name": "Mahape",
      "display_name": "Mahape, Navi Mumbai, Thane, Maharashtra, 400710, India",
      "address": {
        "suburb": "Mahape",
        "city": "Navi Mumbai",
        "state": "Maharashtra",
        "postcode": "400710",
        "country": "India"
      }
    }
    """;

    [Fact]
    public async Task ReverseGeocodeAsync_maps_structured_address_components()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, ReverseJson);

        var details = await Build(handler).ReverseGeocodeAsync(19.10528m, 73.01989m, CancellationToken.None);

        details.Should().NotBeNull();
        details!.City.Should().Be("Navi Mumbai");
        details.State.Should().Be("Maharashtra");
        details.PostalCode.Should().Be("400710");
        details.Country.Should().Be("India");
        details.Address.Should().Contain("Mahape");
    }

    [Fact]
    public async Task ReverseGeocodeAsync_echoes_the_requested_point_not_the_providers_centre()
    {
        // The admin dragged the pin to an exact spot and THAT spot is what gets
        // geofenced. Snapping to the provider's feature centre would move the
        // fence out from under them.
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, ReverseJson);

        var details = await Build(handler).ReverseGeocodeAsync(19.123456m, 73.654321m, CancellationToken.None);

        details!.Latitude.Should().Be(19.123456m);
        details.Longitude.Should().Be(73.654321m);
    }

    [Fact]
    public async Task ReverseGeocodeAsync_returns_null_when_provider_reports_no_place()
    {
        // Nominatim signals "nothing at this point" with an error object.
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, """{ "error": "Unable to geocode" }""");

        var details = await Build(handler).ReverseGeocodeAsync(0.0001m, 0.0001m, CancellationToken.None);

        details.Should().BeNull();
    }

    [Theory]
    [InlineData(91, 0)]
    [InlineData(-91, 0)]
    [InlineData(0, 181)]
    [InlineData(0, -181)]
    public async Task ReverseGeocodeAsync_rejects_out_of_range_coordinates_without_calling_provider(
        decimal lat, decimal lng)
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, ReverseJson);

        var details = await Build(handler).ReverseGeocodeAsync(lat, lng, CancellationToken.None);

        details.Should().BeNull();
        handler.RequestedUrls.Should().BeEmpty();
    }

    [Fact]
    public async Task ReverseGeocodeAsync_returns_null_and_does_not_throw_when_provider_fails()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.InternalServerError, "boom");

        var details = await Build(handler).ReverseGeocodeAsync(19.1m, 73.0m, CancellationToken.None);

        details.Should().BeNull();
    }

    // ── Caching (what keeps us inside OSM's ~1 req/sec policy) ───────────────

    [Fact]
    public async Task SearchAsync_serves_repeat_queries_from_cache()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, TwoResultSearchJson);
        var sut = Build(handler, new LocationOptions
        {
            MinQueryLength = 3,
            TimeoutSeconds = 5,
            CacheMinutes   = 30,
        });

        await sut.SearchAsync("millennium business park", CancellationToken.None);
        await sut.SearchAsync("MILLENNIUM BUSINESS PARK", CancellationToken.None); // case-insensitive
        var third = await sut.SearchAsync("  millennium business park  ", CancellationToken.None); // whitespace-normalised

        third.Should().HaveCount(2);
        handler.RequestedUrls.Should().HaveCount(1,
            "repeat searches must not re-hit a provider limited to ~1 request/second");
    }

    [Fact]
    public async Task SearchAsync_applies_configured_country_bias_and_result_cap()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, "[]");
        var sut = Build(handler, new LocationOptions
        {
            MinQueryLength = 3,
            MaxResults     = 5,
            CountryCodes   = "in",
            CacheMinutes   = 0,
        });

        await sut.SearchAsync("stadium", CancellationToken.None);

        handler.RequestedUrls.Single().Should().Contain("countrycodes=in").And.Contain("limit=5");
    }
}
