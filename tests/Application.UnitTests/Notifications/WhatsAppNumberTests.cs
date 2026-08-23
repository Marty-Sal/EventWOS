using EventWOS.Application.Notifications.Services;
using FluentAssertions;
using Xunit;

namespace EventWOS.Application.UnitTests.Notifications;

/// <summary>
/// Number normalisation. The refusal cases matter most: a wrongly prefixed
/// number is not a failed message, it is a message delivered to a stranger.
/// </summary>
public class WhatsAppNumberTests
{
    [Theory]
    [InlineData("9876543210", "919876543210")]          // bare local, as the app stores it
    [InlineData("+91 98765 43210", "919876543210")]      // pasted from a contact card
    [InlineData("091-98765-43210", "919876543210")]      // trunk prefix and separators
    [InlineData("919876543210", "919876543210")]         // already international
    [InlineData("  9876543210  ", "919876543210")]
    public void Normalises_indian_mobiles(string input, string expected)
        => WhatsAppNumber.Normalize(input).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a number")]
    [InlineData("12345")]                                 // too short to be anything
    [InlineData("1234567890123456789")]                   // absurdly long
    public void Refuses_anything_it_cannot_trust(string? input)
        => WhatsAppNumber.Normalize(input).Should().BeNull();

    [Fact]
    public void Passes_through_a_plausible_foreign_number_instead_of_forcing_india()
    {
        // A UK mobile must not come out with 91 bolted on the front.
        WhatsAppNumber.Normalize("+44 7911 123456").Should().Be("447911123456");
    }

    [Fact]
    public void Honours_a_different_default_country_code()
        => WhatsAppNumber.Normalize("5551234567", defaultCountryCode: "1").Should().Be("15551234567");

    [Fact]
    public void Falls_back_to_india_when_the_configured_code_is_junk()
        => WhatsAppNumber.Normalize("9876543210", defaultCountryCode: "abc").Should().Be("919876543210");
}
