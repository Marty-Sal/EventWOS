using EventOpsOracle.BlazorWeb.Services;
using FluentAssertions;
using Xunit;

namespace EventOpsOracle.Application.UnitTests.BlazorLocation;

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

    /// <summary>~19 km from Racecourse — unambiguously a different venue.</summary>
    private static LocationSuggestion Airoli => new(
        PlaceId: "999",
        Name: "The Universe",
        ShortAddress: "Airoli, Navi Mumbai, Maharashtra",
        FullAddress: "The Universe, Mumbra Bypass Road, Airoli, Navi Mumbai, Maharashtra, 400708, India",
        Latitude: 19.1550m,
        Longitude: 72.9990m,
        City: "Navi Mumbai",
        State: "Maharashtra",
        PostalCode: "400708",
        Country: "India");

    /// <summary>Far from Racecourse AND missing a postcode — Nominatim does this constantly.</summary>
    private static LocationSuggestion ThaneNoPostcode => new(
        PlaceId: "777",
        Name: "The Universe",
        ShortAddress: "Shill Phata, Thane",
        FullAddress: "The Universe, Shill Phata, Shill Gaon, Thane, Thane Subdistrict, Thane, Maharashtra, India",
        Latitude: 19.1550m,
        Longitude: 72.9990m,
        City: "Thane",
        State: "Maharashtra",
        PostalCode: null,
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

    // ── Rule 2b: relocation replaces a stale address block ──────────────────

    [Fact]
    public void Picking_a_venue_far_away_replaces_the_previous_address_block()
    {
        // The reported bug: coordinates were typed in Vile Parle, which
        // reverse-geocoded Nanavati Hospital into the address block. Searching a
        // venue in Airoli then moved the pin and set the name, but left the Vile
        // Parle address sitting next to Airoli coordinates — one venue record
        // describing two different places, saved without complaint.
        var vileParle = Empty with
        {
            AddressLine1 = "Nanavati Hospital, Swami Vivekanand Road, Vile Parle",
            City         = "Mumbai",
            State        = "Maharashtra",
            PostalCode   = "400057",
            Country      = "India",
            Latitude     = 19.105374d,
            Longitude    = 72.839953d,
        };

        var result = VenueLocationMapper.ApplySuggestion(vileParle, Airoli, States);

        result.AddressLine1.Should().Contain("Airoli");
        result.AddressLine1.Should().NotContain("Nanavati");
        result.City.Should().Be("Navi Mumbai");
        result.PostalCode.Should().Be("400708");
        result.Latitude.Should().Be(19.1550d);
    }

    [Fact]
    public void Re_picking_the_same_place_still_protects_hand_typed_text()
    {
        // The pin barely moves, so this is a refinement and the guard holds.
        var current = Empty with
        {
            AddressLine1 = "Gate 3, opposite the paddock",
            City         = "Mumbai Suburban",
            Latitude     = 18.98205d,
            Longitude    = 72.81004d,
        };

        var result = VenueLocationMapper.ApplySuggestion(current, Racecourse, States);

        result.AddressLine1.Should().Be("Gate 3, opposite the paddock");
        result.City.Should().Be("Mumbai Suburban");
    }

    [Fact]
    public void The_admins_venue_name_survives_even_a_relocation()
    {
        // Name is the label the admin chose, not a description of the point.
        var current = Empty with
        {
            Name      = "Client's rooftop — do not rename",
            Latitude  = 18.9820d,
            Longitude = 72.8100d,
        };

        var result = VenueLocationMapper.ApplySuggestion(current, Airoli, States);

        result.Name.Should().Be("Client's rooftop — do not rename");
        result.City.Should().Be("Navi Mumbai");
    }

    [Fact]
    public void Typing_a_whole_new_coordinate_pair_replaces_the_old_address()
    {
        // Same defect reached through the manual latitude/longitude boxes rather
        // than the search box.
        var current = Empty with
        {
            AddressLine1 = "Nanavati Hospital, Vile Parle",
            City         = "Mumbai",
            PostalCode   = "400057",
            Latitude     = 19.105374d,
            Longitude    = 72.839953d,
        };

        var detail = new LocationDetail(
            PlaceId: "999",
            Name: "The Universe",
            Address: "The Universe, Mumbra Bypass Road, Airoli",
            City: "Navi Mumbai",
            State: "Maharashtra",
            PostalCode: "400708",
            Country: "India",
            Latitude: 19.1550m,
            Longitude: 72.9990m);

        var result = VenueLocationMapper.ApplyReverseGeocode(
            current, detail, 19.1550d, 72.9990d, States);

        result.AddressLine1.Should().Contain("Airoli");
        result.City.Should().Be("Navi Mumbai");
        result.PostalCode.Should().Be("400708");
    }

    [Fact]
    public void Nudging_the_pin_a_few_metres_leaves_typed_text_alone()
    {
        // The "drag it to the exact entrance" flow must stay non-destructive.
        var current = Empty with
        {
            AddressLine1 = "Hall 2, service entrance",
            City         = "Mumbai",
            Latitude     = 18.9820d,
            Longitude    = 72.8100d,
        };

        var detail = new LocationDetail(
            PlaceId: "123",
            Name: "Mahalaxmi Racecourse",
            Address: "Railway Sports Ground Lane, Mahalakshmi",
            City: "Mumbai City",
            State: "Maharashtra",
            PostalCode: "400034",
            Country: "India",
            Latitude: 18.98215m,
            Longitude: 72.81012m);

        var result = VenueLocationMapper.ApplyReverseGeocode(
            current, detail, 18.98215d, 72.81012d, States);

        result.AddressLine1.Should().Be("Hall 2, service entrance");
        result.City.Should().Be("Mumbai");
    }

    // ── Rule 2c: relocating CLEARS what the new place did not supply ─────────

    [Fact]
    public void Relocating_clears_a_postcode_the_new_place_did_not_supply()
    {
        // Reported: coordinates in Vile Parle filled 400057, then searching a
        // Thane venue with no postcode kept 400057 — a Vile Parle postcode under
        // a Thane address, saved without complaint.
        var vileParle = Empty with
        {
            AddressLine1 = "Nanavati Hospital, Vile Parle",
            City         = "Mumbai",
            PostalCode   = "400057",
            Country      = "India",
            Latitude     = 19.105374d,
            Longitude    = 72.839953d,
        };

        var result = VenueLocationMapper.ApplySuggestion(vileParle, ThaneNoPostcode, States);

        result.PostalCode.Should().BeNull("the new place supplied none, so the old one cannot stand");
        result.City.Should().Be("Thane");
        result.AddressLine1.Should().Contain("Shill Phata");
    }

    [Fact]
    public void Refining_keeps_a_postcode_the_provider_happens_to_omit()
    {
        // The mirror case: a provider omission must NOT wipe a good value when the
        // pin has barely moved.
        var current = Empty with
        {
            City       = "Mumbai",
            PostalCode = "400034",
            Latitude   = 18.9820d,
            Longitude  = 72.8100d,
        };

        var nearbyNoPostcode = Racecourse with { PostalCode = null, Latitude = 18.98210m, Longitude = 72.81008m };

        var result = VenueLocationMapper.ApplySuggestion(current, nearbyNoPostcode, States);

        result.PostalCode.Should().Be("400034");
    }

    [Fact]
    public void Relocating_by_typed_coordinates_also_clears_a_missing_postcode()
    {
        var current = Empty with
        {
            AddressLine1 = "Nanavati Hospital, Vile Parle",
            City         = "Mumbai",
            PostalCode   = "400057",
            Latitude     = 19.105374d,
            Longitude    = 72.839953d,
        };

        var detail = new LocationDetail(
            PlaceId: "777",
            Name: "The Universe",
            Address: "The Universe, Shill Phata, Thane",
            City: "Thane",
            State: "Maharashtra",
            PostalCode: null,
            Country: "India",
            Latitude: 19.1550m,
            Longitude: 72.9990m);

        var result = VenueLocationMapper.ApplyReverseGeocode(
            current, detail, 19.1550d, 72.9990d, States);

        result.PostalCode.Should().BeNull();
        result.City.Should().Be("Thane");
    }

    [Fact]
    public void The_full_reported_sequence_ends_with_a_consistent_address()
    {
        // coords in Vile Parle -> search a Thane venue -> coords back to Vile
        // Parle. Every step must leave one place described, never a hybrid.
        var afterFirstCoords = VenueLocationMapper.ApplyReverseGeocode(
            Empty,
            new LocationDetail("1", "Nanavati", "Nanavati Hospital, Vile Parle",
                "Mumbai", "Maharashtra", "400057", "India", 19.105374m, 72.839953m),
            19.105374d, 72.839953d, States);

        afterFirstCoords.PostalCode.Should().Be("400057");

        var afterSearch = VenueLocationMapper.ApplySuggestion(afterFirstCoords, ThaneNoPostcode, States);

        afterSearch.City.Should().Be("Thane");
        afterSearch.PostalCode.Should().BeNull();

        // Back to the original point: a relocation again, so the Thane text goes.
        var afterSecondCoords = VenueLocationMapper.ApplyReverseGeocode(
            afterSearch,
            new LocationDetail("1", "Nanavati", "Nanavati Hospital, Vile Parle",
                "Mumbai", "Maharashtra", "400057", "India", 19.105374m, 72.839953m),
            19.105374d, 72.839953d, States);

        afterSecondCoords.AddressLine1.Should().Contain("Nanavati");
        afterSecondCoords.City.Should().Be("Mumbai");
        afterSecondCoords.PostalCode.Should().Be("400057");
    }

    // ── The relocation test itself ───────────────────────────────────────────

    [Fact]
    public void A_first_point_is_never_a_relocation()
    {
        VenueLocationMapper.IsRelocation(null, null, 19.1550d, 72.9990d).Should().BeFalse();
    }

    [Fact]
    public void Nudging_within_the_tolerance_is_not_a_relocation()
    {
        // ~30 m north of the start point.
        VenueLocationMapper.IsRelocation(18.9820d, 72.8100d, 18.98227d, 72.8100d)
            .Should().BeFalse();
    }

    [Fact]
    public void A_point_in_another_suburb_is_a_relocation()
    {
        // Vile Parle -> Airoli, ~19 km.
        VenueLocationMapper.IsRelocation(19.105374d, 72.839953d, 19.1550d, 72.9990d)
            .Should().BeTrue();
    }

    [Fact]
    public void Comparing_a_point_against_itself_is_never_a_relocation()
    {
        // Pins the trap the venue form fell into twice: pass the ALREADY-MOVED
        // coordinates as the origin and every relocation reads as a refinement, so
        // a stale address block survives. The origin must be the point the current
        // address text describes.
        VenueLocationMapper.IsRelocation(19.1550d, 72.9990d, 19.1550d, 72.9990d)
            .Should().BeFalse();
    }

    // ── Name: provider-written follows the pin, hand-typed never does ─────────

    [Fact]
    public void A_provider_written_name_is_replaced_when_the_venue_relocates()
    {
        // Reported: picked "The Universe" (name filled correctly), then searched
        // Jio Garden. Address, city and postcode all moved, but the name stayed
        // "The Universe" above a Bandra Kurla address.
        var afterUniverse = VenueLocationMapper.ApplySuggestion(Empty, ThaneNoPostcode, States);
        afterUniverse.Name.Should().Be("The Universe");

        var jioGarden = new LocationSuggestion(
            PlaceId: "555",
            Name: "Jio Garden",
            ShortAddress: "Bandra Kurla Complex, Mumbai",
            FullAddress: "Jio Garden, Street 3, G Block, Bandra Kurla Complex, Mumbai, Maharashtra, 400051, India",
            Latitude: 19.0620m,
            Longitude: 72.8690m,
            City: "Mumbai",
            State: "Maharashtra",
            PostalCode: "400051",
            Country: "India");

        var result = VenueLocationMapper.ApplySuggestion(afterUniverse, jioGarden, States);

        result.Name.Should().Be("Jio Garden");
        result.City.Should().Be("Mumbai");
        result.PostalCode.Should().Be("400051");
    }

    [Fact]
    public void A_provider_written_name_is_cleared_when_the_new_place_has_none()
    {
        // What the admin asked for explicitly: if nothing comes in, the box empties
        // rather than keeping the previous venue's name.
        var afterUniverse = VenueLocationMapper.ApplySuggestion(Empty, ThaneNoPostcode, States);

        var unnamed = Racecourse with { Name = null, FullAddress = "Railway Sports Ground Lane, Mahalakshmi" };

        var result = VenueLocationMapper.ApplySuggestion(afterUniverse, unnamed, States);

        result.Name.Should().BeNull();
    }

    [Fact]
    public void A_hand_typed_name_still_survives_a_relocation()
    {
        // The reason Name was exempt in the first place. NameFromProvider is null
        // here, so nothing marks this text as ours to overwrite.
        var current = Empty with
        {
            Name      = "Client's rooftop — do not rename",
            Latitude  = 18.9820d,
            Longitude = 72.8100d,
        };

        var result = VenueLocationMapper.ApplySuggestion(current, ThaneNoPostcode, States);

        result.Name.Should().Be("Client's rooftop — do not rename");
        result.City.Should().Be("Thane", "the address still follows the pin");
    }

    [Fact]
    public void A_name_the_admin_edited_after_a_pick_is_no_longer_ours_to_replace()
    {
        // Provider filled it, admin then refined it — that makes it theirs.
        var afterPick = VenueLocationMapper.ApplySuggestion(Empty, ThaneNoPostcode, States);
        var edited    = afterPick with { Name = "The Universe — north lawn" };

        var result = VenueLocationMapper.ApplySuggestion(edited, Racecourse, States);

        result.Name.Should().Be("The Universe — north lawn");
    }

    [Fact]
    public void Refining_the_same_place_leaves_even_a_provider_written_name_alone()
    {
        var afterPick = VenueLocationMapper.ApplySuggestion(Empty, Racecourse, States);
        var nudged    = Racecourse with { Name = "Mahalaxmi Race Course (Gate 1)", Latitude = 18.98207m, Longitude = 72.81003m };

        var result = VenueLocationMapper.ApplySuggestion(afterPick, nudged, States);

        result.Name.Should().Be("Mahalaxmi Racecourse", "a nudge is not a new venue");
    }
}
