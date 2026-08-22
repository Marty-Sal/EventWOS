using EventWOS.Domain.Enums;

namespace EventWOS.Application.Files;

/// <summary>
/// Server-side file validation rules, keyed by DocumentType. This is the
/// ONLY source of truth for allowed size/type — the Blazor client applies
/// the same limits for a fast UX, but per the "never trust client-side
/// validation" requirement, every rule here is re-checked in
/// UploadFileHandler regardless of what the client already checked.
/// </summary>
public static class FileValidationPolicy
{
    public sealed record Rule(long MaxSizeBytes, string[] AllowedContentTypes, string[] AllowedExtensions);

    private static readonly string[] ImageTypes = { "image/jpeg", "image/png", "image/webp" };
    private static readonly string[] ImageExts  = { ".jpg", ".jpeg", ".png", ".webp" };
    private static readonly string[] ImageOrPdfTypes = { "image/jpeg", "image/png", "application/pdf" };
    private static readonly string[] ImageOrPdfExts  = { ".jpg", ".jpeg", ".png", ".pdf" };

    public static Rule For(DocumentType type) => type switch
    {
        DocumentType.CrewProfilePhoto        => new Rule(5  * 1024 * 1024, ImageTypes,      ImageExts),
        DocumentType.CrewIdentificationProof => new Rule(5  * 1024 * 1024, ImageOrPdfTypes, ImageOrPdfExts),
        DocumentType.VendorDocument          => new Rule(10 * 1024 * 1024, ImageOrPdfTypes, ImageOrPdfExts),
        DocumentType.EventDocument           => new Rule(15 * 1024 * 1024, ImageOrPdfTypes, ImageOrPdfExts),
        DocumentType.VendorProfilePhoto      => new Rule(5  * 1024 * 1024, ImageTypes,      ImageExts),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown document type.")
    };

    /// <summary>Identification documents are sensitive PII — always logged on access and gated behind an extra permission.</summary>
    public static bool IsSensitive(DocumentType type) => type == DocumentType.CrewIdentificationProof;

    /// <summary>Only image types get through IImageProcessor (thumbnail + re-encode).</summary>
    public static bool IsImage(DocumentType type) => type is DocumentType.CrewProfilePhoto or DocumentType.VendorProfilePhoto;

    public static string ExtensionForContentType(string contentType) => contentType switch
    {
        "image/jpeg"      => ".jpg",
        "image/png"       => ".png",
        "image/webp"      => ".webp",
        "application/pdf" => ".pdf",
        _ => ".bin"
    };

    /// <summary>
    /// Every extension that legitimately goes with a declared content-type.
    /// Used to reject MISMATCHED pairs — see Validate() below for why passing
    /// both allow-lists separately isn't enough.
    /// </summary>
    private static string[] ExtensionsForContentType(string contentType) => contentType.ToLowerInvariant() switch
    {
        "image/jpeg"      => new[] { ".jpg", ".jpeg" },
        "image/png"       => new[] { ".png" },
        "image/webp"      => new[] { ".webp" },
        "application/pdf" => new[] { ".pdf" },
        _ => Array.Empty<string>()
    };

    /// <summary>
    /// Full server-side check: size, declared content-type, AND the actual
    /// file-extension the client sent (defense in depth — a mismatched
    /// extension/content-type pair is rejected rather than silently trusted).
    /// Does NOT verify the real byte content — see the Content-taking
    /// overload below, which is what UploadFileHandler actually calls.
    /// Kept public/separate because it's cheap to run from FluentValidation
    /// (which validates the command shape before the handler even runs).
    /// </summary>
    public static (bool IsValid, string? Error) Validate(DocumentType type, long sizeBytes, string contentType, string originalFileName)
    {
        var rule = For(type);
        if (sizeBytes <= 0)
            return (false, "File is empty.");
        if (sizeBytes > rule.MaxSizeBytes)
            return (false, $"File exceeds the {rule.MaxSizeBytes / (1024 * 1024)}MB limit for this document type.");
        if (!rule.AllowedContentTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase))
            return (false, $"Content-type '{contentType}' is not allowed for this document type.");

        var ext = Path.GetExtension(originalFileName);
        if (string.IsNullOrEmpty(ext) || !rule.AllowedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            return (false, $"File extension '{ext}' is not allowed for this document type.");

        // The extension and the content-type must also agree with EACH OTHER.
        // Checking them only against their own allow-lists leaves a hole: for a
        // profile photo, "image/jpeg" and ".png" are both individually allowed,
        // so a file declaring one and named the other sailed through — exactly
        // the shape of a spoofing attempt, and it would then be stored with a
        // filename that contradicts how it gets served back.
        var validExts = ExtensionsForContentType(contentType);
        if (validExts.Length > 0 && !validExts.Contains(ext, StringComparer.OrdinalIgnoreCase))
            return (false, $"File extension '{ext}' does not match the declared content-type '{contentType}'.");

        return (true, null);
    }

    /// <summary>
    /// Same checks as above PLUS a magic-byte signature check against the
    /// actual file content. The client-declared Content-Type header and the
    /// filename extension are BOTH attacker-controlled strings — without
    /// this, uploading e.g. an HTML/script payload renamed "id.pdf" with a
    /// spoofed "application/pdf" Content-Type would sail straight through.
    /// This is the check UploadFileHandler actually relies on.
    /// </summary>
    public static (bool IsValid, string? Error) Validate(DocumentType type, long sizeBytes, string contentType, string originalFileName, ReadOnlySpan<byte> content)
    {
        var (isValid, error) = Validate(type, sizeBytes, contentType, originalFileName);
        if (!isValid) return (isValid, error);

        if (!FileSignatureValidator.MatchesSignature(contentType, content))
            return (false, "File content does not match its declared type. The file may be corrupted or mislabeled.");

        return (true, null);
    }
}

/// <summary>
/// Magic-byte (file-signature) checks. Extension and Content-Type are both
/// client-supplied and trivially spoofable — this looks at the actual first
/// bytes on the wire, which a client cannot fake without also breaking the
/// file for its stated purpose.
/// </summary>
public static class FileSignatureValidator
{
    public static bool MatchesSignature(string contentType, ReadOnlySpan<byte> content) => contentType.ToLowerInvariant() switch
    {
        "image/jpeg" => content.Length >= 3 && content[0] == 0xFF && content[1] == 0xD8 && content[2] == 0xFF,
        "image/png"  => content.Length >= 8 &&
                         content[0] == 0x89 && content[1] == 0x50 && content[2] == 0x4E && content[3] == 0x47 &&
                         content[4] == 0x0D && content[5] == 0x0A && content[6] == 0x1A && content[7] == 0x0A,
        "image/webp" => content.Length >= 12 &&
                         content[0] == 0x52 && content[1] == 0x49 && content[2] == 0x46 && content[3] == 0x46 && // "RIFF"
                         content[8] == 0x57 && content[9] == 0x45 && content[10] == 0x42 && content[11] == 0x50, // "WEBP"
        "application/pdf" => content.Length >= 5 &&
                         content[0] == 0x25 && content[1] == 0x50 && content[2] == 0x44 && content[3] == 0x46 && content[4] == 0x2D, // "%PDF-"
        // Unknown content-type — FileValidationPolicy.Validate() already rejected anything
        // not in a DocumentType's AllowedContentTypes list before we'd ever get here, so
        // there is no allow-listed type without a signature check above.
        _ => false
    };
}
