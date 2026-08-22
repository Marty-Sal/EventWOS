using EventWOS.BlazorWeb.Services;
using FluentAssertions;
using Xunit;

namespace EventWOS.Application.UnitTests.BlazorLocation;

/// <summary>
/// Covers the venue form's location mapping rules.
///
/// These exist because of a real regression: the rules were inline in
/// Venue.razor, and an edit that added the city/state/postcode block silently
/// deleted the two lines assigning the coordinates. Picking a place filled the
/// address perfectly and left the map at the country-wide fallback. The first
/// test here is the one that would have caught it.
/// </summary>
public class VenueLocationMapperTests
{
    private static readonly IReadOnlyList<string> States = new[]
    {
        "Maharashtra", "Karnataka", "Delhi", "Odisha", "Puducherry", "Uttarakhand", "Tamil Nadu",
    };

    private static VenueLocationFields Empty => new(
        Name: null, AddressLine1: null, ShortAddress: null, City: null, State: null,
        PostalCode: null, Country: null, Latitude: null, Longitude: null);

    private static LocationSuggestion Racecourse => new(
        PlaceId: "123",
        Name: "Mahalaxmi Racecourse",
        ShortAddress: "Mahalakshmi, Mumbai, Maharashtra",
        FullAddress: "Mahalaxmi Racecourse, Railway Sports Ground Lane, Mahalakshmi, Mumbai, Maharashtra, 400034, India",
        Latitude: 18.9820m,
        Longitude: 72.8100m,
        City: "Mumbai",
        State: "Maharashtra",
        PostalCode: "400034",
        Country: "India");

    // ── Rule 1: coordinates always win ──────────────────────────────────────

    [Fact]
    public void Picking_a_suggestion_sets_the_coordinates()
    {
        // THE regression test. Without the coordinates the map never moves and the
        // venue can never be geofenced, no matter how complete the address looks.
        var result = VenueLocationMapper.ApplySuggestion(Empty, Racecourse, States);

        result.Latitude.Should().Be(18.9820d);
        result.Longitude.Should().Be(72.8100d);
    }

    [Fact]
    public void Picking_a_suggestion_overwrites_existing_coordinates()
    {
        // Unlike the address text, coordinates are NOT fill-blanks-only: choosing
        // a different place must move the pin off the previous one.
        var current = Empty with { Latitude = 12.9716d, Longitude = 77.5946d };

        var result = VenueLocationMapper.ApplySuggestion(current, Racecourse, States);

        result.Latitude.Should().Be(18.9820d);
        result.Longitude.Should().Be(72.8100d);
    }

    [Fact]
    public void Dragging_the_pin_applies_the_dragged_point_even_with_no_address_found()
    {
        // Provider returned nothing for that point. The coordinates are still the
        // authoritative thing the geofence uses, so they must land regardless.
        var result = VenueLocationMapper.ApplyReverseGeocode(
            Empty, detail: null, latitude: 19.1d, longitude: 72.9d, States);

        result.Latitude.Should().Be(19.1d);
        result.Longitude.Should().Be(72.9d);
        result.City.Should().BeNull();
    }

    // ── Rule 2: address text fills blanks only ──────────────────────────────

    [Fact]
    public void Picking_a_suggestion_fills_the_whole_address_block_when_empty()
    {
        var result = VenueLocationMapper.ApplySuggestion(Empty, Racecourse, States);

        result.Name.Should().Be("Mahalaxmi Racecourse");
        result.City.Should().Be("Mumbai");
        result.State.Should().Be("Maharashtra");
        result.PostalCode.Should().Be("400034");
        result.Country.Should().Be("India");
    }

    [Fact]
    public void Hand_typed_address_values_are_never_overwritten()
    {
        // Geocoders are regularly wrong about unit numbers and hall names, so the
        // admin's own text wins.
        var current = Empty with
        {
            Name = "Racecourse — Gate 3",
            City = "Mumbai Suburban",
            PostalCode = "400001",
        };

        var result = VenueLocationMapper.ApplySuggestion(current, Racecourse, States);

        result.Name.Should().Be("Racecourse — Gate 3");
        result.City.Should().Be("Mumbai Suburban");
        result.PostalCode.Should().Be("400001");
        // ...but the coordinates still moved.
        result.Latitude.Should().Be(18.9820d);
    }

    [Fact]
    public void Long_provider_strings_are_truncated_to_the_column_widths()
    {
        // A save must fail on real validation problems, not on a length the admin
        // never chose. FullAddress here is well over the 200-char column.
        var longish = new string('x', 400);
        var suggestion = Racecourse with { Name = longish, FullAddress = longish, ShortAddress = longish };

        var result = VenueLocationMapper.ApplySuggestion(Empty, suggestion, States);

        result.Name!.Length.Should().Be(VenueLocationMapper.NameMaxLength);
        result.AddressLine1!.Length.Should().Be(VenueLocationMapper.AddressLineMaxLength);
        result.ShortAddress!.Length.Should().Be(VenueLocationMapper.ShortAddressMaxLength);
    }

    [Fact]
    public void A_suggestion_with_no_structured_components_still_sets_coordinates()
    {
        // Remote landmarks genuinely come back with no city/state/postcode.
        var sparse = Racecourse with { City = null, State = null, PostalCode = null, Country = null };

        var result = VenueLocationMapper.ApplySuggestion(Empty, sparse, States);

        result.Latitude.Should().Be(18.9820d);
        result.City.Should().BeNull();
        result.State.Should().BeNull();
    }

    // ── State normalisation ─────────────────────────────────────────────────

    [Theory]
    [InlineData("Maharashtra", "Maharashtra")]
    [InlineData("maharashtra", "Maharashtra")]   // provider casing varies
    [InlineData("Orissa", "Odisha")]             // OSM still carries the old name
    [InlineData("Pondicherry", "Puducherry")]
    [InlineData("Uttaranchal", "Uttarakhand")]
    [InlineData("NCT of Delhi", "Delhi")]
    [InlineData("National Capital Territory of Delhi", "Delhi")]
    [InlineData("Maharashtra State", "Maharashtra")]   // single containment match
    public void Provider_state_names_map_onto_the_dropdown_options(string provider, string expected)
        => VenueLocationMapper.MatchCanonicalState(provider, States).Should().Be(expected);

    [Theory]
    [InlineData("Sindh")]        // not an Indian state
    [InlineData("")]
    [InlineData(null)]
    public void An_unmatched_state_returns_null_rather_than_a_guess(string? provider)
    {
        // StateSelect is a plain select: a non-matching value renders as
        // "Select state…" anyway. A blank the admin must fill is honest; a
        // silently-wrong selection is not.
        VenueLocationMapper.MatchCanonicalState(provider, States).Should().BeNull();
    }

    [Fact]
    public void State_is_left_blank_when_the_canonical_list_could_not_be_loaded()
    {
        // The states endpoint failed. Guessing an unselectable value would look
        // like the pick silently did nothing.
        var result = VenueLocationMapper.ApplySuggestion(Empty, Racecourse, Array.Empty<string>());

        result.State.Should().BeNull();
        result.Latitude.Should().Be(18.9820d);   // coordinates unaffected
    }
}
