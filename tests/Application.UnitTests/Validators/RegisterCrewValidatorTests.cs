using EventWOS.Application.Registration.Commands;
using EventWOS.Application.Registration.Validators;
using FluentValidation.TestHelper;
using Xunit;

namespace EventWOS.Application.UnitTests.Validators;

/// <summary>
/// Locks in the registration contract for Crew self-signup.
///
/// The single most important rule (and the reason this test file exists)
/// is: <b>ReferralCode is mandatory.</b> Without it, anyone could create a
/// crew account that doesn't belong to a vendor — bypassing the vetting
/// the platform relies on. The validator AND the handler both enforce it
/// (defence in depth), so this test ensures we never accidentally relax
/// the validator side and fall back to handler-only enforcement.
/// </summary>
public sealed class RegisterCrewValidatorTests
{
    private readonly RegisterCrewValidator _sut = new();

    private static RegisterCrewCommand Valid(
        string? username = "crew_jane",
        string? email = "jane@example.com",
        string? mobile = "+919876543210",
        string? password = "Passw0rd1",
        string? fullName = "Jane Doe",
        string? referralCode = "ABC123",
        int? experience = 3)
        => new(
            Username:        username!,
            Email:           email!,
            Mobile:          mobile!,
            Password:        password!,
            FullName:        fullName!,
            ReferralCode:    referralCode,
            City:            "Mumbai",
            Skills:          "Rigging",
            ExperienceYears: experience,
            Bio:             null);

    // ── Baseline ─────────────────────────────────────────────────────────────

    [Fact]
    public void Valid_command_has_no_errors()
    {
        _sut.TestValidate(Valid()).ShouldNotHaveAnyValidationErrors();
    }

    // ── ReferralCode (the Phase 5 rule we mustn't lose) ──────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ReferralCode_missing_fails(string? code)
    {
        var result = _sut.TestValidate(Valid(referralCode: code));
        result.ShouldHaveValidationErrorFor(x => x.ReferralCode)
              .WithErrorMessage("A vendor referral code is required to register as crew. Please ask your vendor for their code.");
    }

    [Fact]
    public void ReferralCode_too_long_fails()
    {
        var result = _sut.TestValidate(Valid(referralCode: new string('A', 21)));
        result.ShouldHaveValidationErrorFor(x => x.ReferralCode);
    }

    [Fact]
    public void ReferralCode_at_max_length_passes()
    {
        var result = _sut.TestValidate(Valid(referralCode: new string('A', 20)));
        result.ShouldNotHaveValidationErrorFor(x => x.ReferralCode);
    }

    // ── Username (login identifier — character-set is locked) ────────────────

    [Theory]
    [InlineData("ab")]                              // too short
    [InlineData("")]                                // empty
    [InlineData("jane doe")]                        // space — disallowed
    [InlineData("jane@doe")]                        // @ — disallowed
    [InlineData("jane/doe")]                        // slash — disallowed
    public void Username_invalid_fails(string username)
    {
        var result = _sut.TestValidate(Valid(username: username));
        result.ShouldHaveValidationErrorFor(x => x.Username);
    }

    [Theory]
    [InlineData("jane_doe")]
    [InlineData("jane.doe")]
    [InlineData("jane-doe-99")]
    [InlineData("ABC")]
    public void Username_valid_passes(string username)
    {
        var result = _sut.TestValidate(Valid(username: username));
        result.ShouldNotHaveValidationErrorFor(x => x.Username);
    }

    // ── Password (delegates to PasswordRules — covered separately, but
    //              we still smoke-test the wiring here) ────────────────────────

    [Theory]
    [InlineData("short1")]       // < 8 chars
    [InlineData("nodigitshere")] // no digit
    [InlineData("12345678")]     // no letter
    [InlineData("")]
    public void Password_invalid_fails(string password)
    {
        var result = _sut.TestValidate(Valid(password: password));
        result.ShouldHaveValidationErrorFor(x => x.Password);
    }

    [Fact]
    public void Password_valid_passes() =>
        _sut.TestValidate(Valid(password: "Passw0rd1"))
            .ShouldNotHaveValidationErrorFor(x => x.Password);

    // ── Mobile ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("123")]                  // too short
    [InlineData("+12345678901234567")]   // too long
    [InlineData("abcdefghij")]           // letters
    public void Mobile_invalid_fails(string mobile)
    {
        var result = _sut.TestValidate(Valid(mobile: mobile));
        result.ShouldHaveValidationErrorFor(x => x.Mobile);
    }

    [Theory]
    [InlineData("+919876543210")]
    [InlineData("9876543210")]
    public void Mobile_valid_passes(string mobile)
    {
        var result = _sut.TestValidate(Valid(mobile: mobile));
        result.ShouldNotHaveValidationErrorFor(x => x.Mobile);
    }

    // ── ExperienceYears (optional, but bounded when provided) ────────────────

    [Theory]
    [InlineData(-1)]
    [InlineData(61)]
    public void ExperienceYears_out_of_range_fails(int years)
    {
        var result = _sut.TestValidate(Valid(experience: years));
        result.ShouldHaveValidationErrorFor(x => x.ExperienceYears);
    }

    [Fact]
    public void ExperienceYears_null_is_fine() =>
        _sut.TestValidate(Valid(experience: null))
            .ShouldNotHaveValidationErrorFor(x => x.ExperienceYears);
}
