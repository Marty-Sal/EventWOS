using EventWOS.Application.Registration.Validators;
using FluentAssertions;
using Xunit;

namespace EventWOS.Application.UnitTests.Validators;

/// <summary>
/// PasswordRules is reused by registration, reset, and setup-password flows
/// — so a regression here silently weakens every credentialled entry point.
/// These tests pin the contract: ≥ 8 chars, ≥ 1 letter, ≥ 1 digit.
/// </summary>
public sealed class PasswordRulesTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short1")]        // 6 chars
    [InlineData("1234567a")]      // 8 chars, letter + digit — passes (sanity below)
    public void IsValid_handles_edge_cases(string? input)
    {
        // The 8-char letter+digit case should be the only one that returns true.
        var expected = input == "1234567a";
        PasswordRules.IsValid(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("abcdefgh")]      // no digit
    [InlineData("12345678")]      // no letter
    [InlineData("        ")]      // whitespace only
    public void IsValid_rejects_missing_class(string input) =>
        PasswordRules.IsValid(input).Should().BeFalse();

    [Theory]
    [InlineData("Passw0rd")]
    [InlineData("aaaaaaa1")]
    [InlineData("Z0zzzzzz")]
    [InlineData("very_long_passw0rd_with_underscores")]
    public void IsValid_accepts_well_formed(string input) =>
        PasswordRules.IsValid(input).Should().BeTrue();
}
