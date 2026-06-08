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
/// </summary>
public sealed class RegisterVendorValidatorTests
{
    private readonly RegisterVendorValidator _sut = new();

    private static RegisterVendorCommand Valid(
        string? businessName = "Acme Events",
        string? fullName = "John Smith",
        string? website = "https://acme.example.com",
        string? gst = "GSTIN-1234567890")
        => new(
            Username:          "vendor_john",
            Email:             "john@acme.com",
            Mobile:            "+919876543210",
            Password:          "Passw0rd1",
            FullName:          fullName!,
            BusinessName:      businessName!,
            ContactPersonName: "John Smith",
            GstNumber:         gst,
            Address:           "1 Marine Drive",
            City:              "Mumbai",
            State:             "MH",
            Website:           website,
            Bio:               null);

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
}
