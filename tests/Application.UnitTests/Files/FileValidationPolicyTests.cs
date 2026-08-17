using EventWOS.Application.Files;
using EventWOS.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace EventWOS.Application.UnitTests.Files;

/// <summary>
/// Pins the server-side file validation rules. This is the ONLY authority
/// on allowed size/type — per "never trust client-side validation", these
/// checks are re-run in UploadFileHandler regardless of what the Blazor
/// client already enforced, so a regression here is a real security bug,
/// not just a UX one.
/// </summary>
public sealed class FileValidationPolicyTests
{
    [Fact]
    public void Profile_photo_within_limits_is_valid()
    {
        var (ok, error) = FileValidationPolicy.Validate(
            DocumentType.CrewProfilePhoto, sizeBytes: 2 * 1024 * 1024, contentType: "image/jpeg", originalFileName: "selfie.jpg");
        ok.Should().BeTrue();
        error.Should().BeNull();
    }

    [Fact]
    public void Profile_photo_over_5MB_is_rejected()
    {
        var (ok, error) = FileValidationPolicy.Validate(
            DocumentType.CrewProfilePhoto, sizeBytes: 6 * 1024 * 1024, contentType: "image/jpeg", originalFileName: "selfie.jpg");
        ok.Should().BeFalse();
        error.Should().Contain("5MB");
    }

    [Fact]
    public void Profile_photo_as_pdf_is_rejected_even_though_pdf_is_allowed_for_other_types()
    {
        var (ok, _) = FileValidationPolicy.Validate(
            DocumentType.CrewProfilePhoto, sizeBytes: 1024, contentType: "application/pdf", originalFileName: "selfie.pdf");
        ok.Should().BeFalse();
    }

    [Fact]
    public void Identification_proof_accepts_pdf_up_to_8MB()
    {
        var (ok, _) = FileValidationPolicy.Validate(
            DocumentType.CrewIdentificationProof, sizeBytes: 7 * 1024 * 1024, contentType: "application/pdf", originalFileName: "aadhaar.pdf");
        ok.Should().BeTrue();
    }

    [Fact]
    public void Mismatched_extension_vs_declared_content_type_is_rejected()
    {
        // Declares image/jpeg but the filename says .png — classic spoofing attempt; must fail.
        var (ok, error) = FileValidationPolicy.Validate(
            DocumentType.CrewProfilePhoto, sizeBytes: 1024, contentType: "image/jpeg", originalFileName: "photo.png");
        ok.Should().BeFalse();
        error.Should().Contain("extension");
    }

    [Fact]
    public void Empty_file_is_rejected()
    {
        var (ok, error) = FileValidationPolicy.Validate(
            DocumentType.CrewProfilePhoto, sizeBytes: 0, contentType: "image/jpeg", originalFileName: "empty.jpg");
        ok.Should().BeFalse();
        error.Should().Contain("empty");
    }

    [Theory]
    [InlineData(DocumentType.CrewIdentificationProof, true)]
    [InlineData(DocumentType.CrewProfilePhoto, false)]
    [InlineData(DocumentType.VendorDocument, false)]
    [InlineData(DocumentType.EventDocument, false)]
    public void Only_identification_proof_is_flagged_sensitive(DocumentType type, bool expectedSensitive)
        => FileValidationPolicy.IsSensitive(type).Should().Be(expectedSensitive);

    [Theory]
    [InlineData("image/jpeg", ".jpg")]
    [InlineData("image/png", ".png")]
    [InlineData("application/pdf", ".pdf")]
    public void Extension_is_derived_from_content_type_not_trusted_from_client(string contentType, string expectedExt)
        => FileValidationPolicy.ExtensionForContentType(contentType).Should().Be(expectedExt);
}
