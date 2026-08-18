using EventWOS.Application.Files;
using EventWOS.Application.Registration.Commands;
using EventWOS.Application.Registration.Validators;
using FluentValidation.TestHelper;
using Xunit;

namespace EventWOS.Application.UnitTests.Validators;

/// <summary>
/// Locks in the registration contract for Vendor self-signup.
/// Key invariants:
///   - Username + Email + Mobile + Password + FullName + BusinessName are required.
///   - Vendor registration has NO referral code (vendors are the top of the tree).
///   - Mobile/FullName rules match Crew's (exactly 10 digits; letters+spaces only).
///   - ProfilePhoto is optional but validated (type/size) when provided.
/// </summary>
public sealed class RegisterVendorValidatorTests
{
    private readonly RegisterVendorValidator _sut = new();

    private static RegisterVendorCommand Valid(
        string? businessName = "Acme Events",
        string? fullName = "John Smith",
        string? mobile = "9876543210",
        string? website = "https://acme.example.com",
        string? gst = "GSTIN-1234567890",
        FileUploadPayload? profilePhoto = null)
        => new(
            Username:          "vendor_john",
            Email:             "john@acme.com",
            Mobile:            mobile!,
            Password:          "Passw0rd1",
            FullName:          fullName!,
            BusinessName:      businessName!,
            ContactPersonName: "John Smith",
            GstNumber:         gst,
            Address:           "1 Marine Drive",
            City:              "Mumbai",
            State:             "MH",
            Website:           website,
            Bio:               null,
            ProfilePhoto:      profilePhoto);

    [Fact]
    public void Valid_command_has_no_errors() =>
        _sut.TestValidate(Valid()).ShouldNotHaveAnyValidationErrors();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BusinessName_required(string? name)
    {
        var result = _sut.TestValidate(Valid(businessName: name));
        result.ShouldHaveValidationErrorFor(x => x.BusinessName);
    }

    [Fact]
    public void BusinessName_too_long_fails()
    {
        var result = _sut.TestValidate(Valid(businessName: new string('X', 201)));
        result.ShouldHaveValidationErrorFor(x => x.BusinessName);
    }

    [Fact]
    public void GstNumber_optional_when_null() =>
        _sut.TestValidate(Valid(gst: null))
            .ShouldNotHaveValidationErrorFor(x => x.GstNumber);

    [Fact]
    public void Website_too_long_fails()
    {
        var result = _sut.TestValidate(Valid(website: "https://" + new string('a', 250) + ".com"));
        result.ShouldHaveValidationErrorFor(x => x.Website);
    }

    [Fact]
    public void Website_null_is_fine() =>
        _sut.TestValidate(Valid(website: null))
            .ShouldNotHaveValidationErrorFor(x => x.Website);

    // ── FullName (letters and spaces only — aligned with Crew) ────────────────

    [Theory]
    [InlineData("John123")]
    [InlineData("John_Smith")]
    [InlineData("")]
    public void FullName_invalid_fails(string fullName)
    {
        var result = _sut.TestValidate(Valid(fullName: fullName));
        result.ShouldHaveValidationErrorFor(x => x.FullName);
    }

    [Fact]
    public void FullName_valid_passes() =>
        _sut.TestValidate(Valid(fullName: "John Smith"))
            .ShouldNotHaveValidationErrorFor(x => x.FullName);

    // ── Mobile (exactly 10 digits — tightened from the old 10-15/+prefix rule) ─

    [Theory]
    [InlineData("+919876543210")]  // country code no longer accepted
    [InlineData("123")]
    [InlineData("98765432100")]    // 11 digits
    [InlineData("abcdefghij")]
    public void Mobile_invalid_fails(string mobile)
    {
        var result = _sut.TestValidate(Valid(mobile: mobile));
        result.ShouldHaveValidationErrorFor(x => x.Mobile);
    }

    [Fact]
    public void Mobile_valid_passes() =>
        _sut.TestValidate(Valid(mobile: "9876543210"))
            .ShouldNotHaveValidationErrorFor(x => x.Mobile);

    // ── ProfilePhoto (optional — but validated when present) ──────────────────

    [Fact]
    public void ProfilePhoto_null_is_fine() =>
        _sut.TestValidate(Valid(profilePhoto: null))
            .ShouldNotHaveValidationErrorFor(x => x.ProfilePhoto);

    [Fact]
    public void ProfilePhoto_valid_passes()
    {
        var photo = new FileUploadPayload(new byte[] { 1, 2, 3 }, "logo.jpg", "image/jpeg");
        _sut.TestValidate(Valid(profilePhoto: photo)).ShouldNotHaveValidationErrorFor(x => x.ProfilePhoto);
    }

    [Fact]
    public void ProfilePhoto_wrong_content_type_fails()
    {
        var badFile = new FileUploadPayload(new byte[] { 1, 2 }, "sheet.xlsx",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        var result = _sut.TestValidate(Valid(profilePhoto: badFile));
        result.ShouldHaveValidationErrorFor(x => x.ProfilePhoto);
    }

    [Fact]
    public void ProfilePhoto_too_large_fails()
    {
        var bigFile = new FileUploadPayload(new byte[6 * 1024 * 1024], "logo.jpg", "image/jpeg");
        var result = _sut.TestValidate(Valid(profilePhoto: bigFile));
        result.ShouldHaveValidationErrorFor(x => x.ProfilePhoto);
    }
}
