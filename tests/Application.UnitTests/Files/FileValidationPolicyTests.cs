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

    // ─── Magic-byte signature checks ────────────────────────────────────────
    // Extension and Content-Type are both attacker-controlled strings. These
    // pin the one check that actually looks at the bytes on the wire.

    private static readonly byte[] RealJpegBytes = { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 };
    private static readonly byte[] RealPdfBytes  = System.Text.Encoding.ASCII.GetBytes("%PDF-1.7
rest of file");
    private static readonly byte[] FakeHtmlBytes = System.Text.Encoding.ASCII.GetBytes("<html><script>evil()</script></html>");

    [Fact]
    public void Real_jpeg_bytes_with_matching_declared_type_is_accepted()
    {
        var (ok, _) = FileValidationPolicy.Validate(
            DocumentType.CrewProfilePhoto, RealJpegBytes.Length, "image/jpeg", "selfie.jpg", RealJpegBytes);
        ok.Should().BeTrue();
    }

    [Fact]
    public void Html_payload_disguised_as_pdf_is_rejected_by_signature_check()
    {
        // Attacker declares application/pdf and names it identity.pdf, but the
        // actual bytes are an HTML/script payload — extension + Content-Type
        // both "pass", so only the signature check can catch this.
        var (ok, error) = FileValidationPolicy.Validate(
            DocumentType.CrewIdentificationProof, FakeHtmlBytes.Length, "application/pdf", "identity.pdf", FakeHtmlBytes);
        ok.Should().BeFalse();
        error.Should().Contain("does not match its declared type");
    }

    [Fact]
    public void Html_payload_disguised_as_jpeg_is_rejected_by_signature_check()
    {
        var (ok, _) = FileValidationPolicy.Validate(
            DocumentType.CrewProfilePhoto, FakeHtmlBytes.Length, "image/jpeg", "selfie.jpg", FakeHtmlBytes);
        ok.Should().BeFalse();
    }

    [Fact]
    public void Real_pdf_bytes_for_vendor_document_is_accepted()
    {
        var (ok, _) = FileValidationPolicy.Validate(
            DocumentType.VendorDocument, RealPdfBytes.Length, "application/pdf", "contract.pdf", RealPdfBytes);
        ok.Should().BeTrue();
    }

    [Theory]
    [InlineData("image/jpeg")]
    [InlineData("image/png")]
    [InlineData("image/webp")]
    [InlineData("application/pdf")]
    public void Empty_content_never_matches_any_signature(string contentType)
        => FileSignatureValidator.MatchesSignature(contentType, Array.Empty<byte>()).Should().BeFalse();
}
