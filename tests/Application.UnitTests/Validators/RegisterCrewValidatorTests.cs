using EventWOS.Application.Files;
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
///
/// Also locks in the newer rules added alongside file upload support:
/// 18+ DateOfBirth and a mandatory IdentificationProof file.
/// </summary>
public sealed class RegisterCrewValidatorTests
{
    private readonly RegisterCrewValidator _sut = new();

    private static readonly FileUploadPayload ValidIdProof =
        new(new byte[] { 1, 2, 3, 4 }, "aadhaar.jpg", "image/jpeg");

    private static DateTime AdultDob => DateTime.UtcNow.Date.AddYears(-25);

    private static RegisterCrewCommand Valid(
        string? username = "crew_jane",
        string? email = "jane@example.com",
        string? mobile = "9876543210",
        string? password = "Passw0rd1",
        string? fullName = "Jane Doe",
        DateTime? dateOfBirth = null,
        string? referralCode = "ABC123",
        int? experience = 3,
        FileUploadPayload? identificationProof = null)
        => new(
            Username:            username!,
            Email:               email!,
            Mobile:              mobile!,
            Password:            password!,
            FullName:            fullName!,
            DateOfBirth:         dateOfBirth ?? AdultDob,
            ReferralCode:        referralCode,
            City:                "Mumbai",
            Skills:              "Rigging",
            ExperienceYears:     experience,
            Bio:                 null,
            IdentificationProof: identificationProof ?? ValidIdProof,
            ProfilePhoto:        null);

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

    // ── FullName (letters and spaces only) ────────────────────────────────────

    [Theory]
    [InlineData("Jane123")]
    [InlineData("Jane_Doe")]
    [InlineData("Jane-Doe")]
    [InlineData("")]
    [InlineData("   ")]
    public void FullName_invalid_fails(string fullName)
    {
        var result = _sut.TestValidate(Valid(fullName: fullName));
        result.ShouldHaveValidationErrorFor(x => x.FullName);
    }

    [Theory]
    [InlineData("Jane Doe")]
    [InlineData("Jane")]
    [InlineData("Mary Anne Smith")]
    public void FullName_valid_passes(string fullName)
    {
        var result = _sut.TestValidate(Valid(fullName: fullName));
        result.ShouldNotHaveValidationErrorFor(x => x.FullName);
    }

    // ── Mobile (exactly 10 digits — tightened, no +country code / separators) ──

    [Theory]
    [InlineData("123")]                  // too short
    [InlineData("+919876543210")]        // has country code — no longer accepted
    [InlineData("98765432100")]          // 11 digits
    [InlineData("abcdefghij")]           // letters
    public void Mobile_invalid_fails(string mobile)
    {
        var result = _sut.TestValidate(Valid(mobile: mobile));
        result.ShouldHaveValidationErrorFor(x => x.Mobile);
    }

    [Fact]
    public void Mobile_valid_passes()
    {
        var result = _sut.TestValidate(Valid(mobile: "9876543210"));
        result.ShouldNotHaveValidationErrorFor(x => x.Mobile);
    }

    // ── DateOfBirth (18+, no future/absurd dates) ─────────────────────────────

    [Fact]
    public void DateOfBirth_under_18_fails()
    {
        var result = _sut.TestValidate(Valid(dateOfBirth: DateTime.UtcNow.Date.AddYears(-17)));
        result.ShouldHaveValidationErrorFor(x => x.DateOfBirth);
    }

    [Fact]
    public void DateOfBirth_exactly_18_passes()
    {
        // Exactly 18 years ago today — should just clear the bar.
        var result = _sut.TestValidate(Valid(dateOfBirth: DateTime.UtcNow.Date.AddYears(-18)));
        result.ShouldNotHaveValidationErrorFor(x => x.DateOfBirth);
    }

    [Fact]
    public void DateOfBirth_in_future_fails()
    {
        var result = _sut.TestValidate(Valid(dateOfBirth: DateTime.UtcNow.Date.AddDays(1)));
        result.ShouldHaveValidationErrorFor(x => x.DateOfBirth);
    }

    [Fact]
    public void DateOfBirth_absurdly_old_fails()
    {
        var result = _sut.TestValidate(Valid(dateOfBirth: DateTime.UtcNow.Date.AddYears(-101)));
        result.ShouldHaveValidationErrorFor(x => x.DateOfBirth);
    }

    // ── IdentificationProof (mandatory) ────────────────────────────────────────

    [Fact]
    public void IdentificationProof_missing_fails()
    {
        var result = _sut.TestValidate(Valid(identificationProof: null!));
        result.ShouldHaveValidationErrorFor(x => x.IdentificationProof)
              .WithErrorMessage("Identification proof (Aadhaar card, driving licence, or voter ID) is required.");
    }

    [Fact]
    public void IdentificationProof_wrong_content_type_fails()
    {
        var badFile = new FileUploadPayload(new byte[] { 1, 2 }, "sheet.xlsx",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        var result = _sut.TestValidate(Valid(identificationProof: badFile));
        result.ShouldHaveValidationErrorFor(x => x.IdentificationProof);
    }

    [Fact]
    public void IdentificationProof_too_large_fails()
    {
        var bigFile = new FileUploadPayload(new byte[6 * 1024 * 1024], "id.jpg", "image/jpeg");
        var result = _sut.TestValidate(Valid(identificationProof: bigFile));
        result.ShouldHaveValidationErrorFor(x => x.IdentificationProof);
    }

    [Fact]
    public void IdentificationProof_valid_passes()
    {
        var result = _sut.TestValidate(Valid(identificationProof: ValidIdProof));
        result.ShouldNotHaveValidationErrorFor(x => x.IdentificationProof);
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
