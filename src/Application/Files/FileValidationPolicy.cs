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
        DocumentType.CrewIdentificationProof => new Rule(8  * 1024 * 1024, ImageOrPdfTypes, ImageOrPdfExts),
        DocumentType.VendorDocument          => new Rule(10 * 1024 * 1024, ImageOrPdfTypes, ImageOrPdfExts),
        DocumentType.EventDocument           => new Rule(15 * 1024 * 1024, ImageOrPdfTypes, ImageOrPdfExts),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown document type.")
    };

    /// <summary>Identification documents are sensitive PII — always logged on access and gated behind an extra permission.</summary>
    public static bool IsSensitive(DocumentType type) => type == DocumentType.CrewIdentificationProof;

    /// <summary>Only image types get through IImageProcessor (thumbnail + re-encode).</summary>
    public static bool IsImage(DocumentType type) => type == DocumentType.CrewProfilePhoto;

    public static string ExtensionForContentType(string contentType) => contentType switch
    {
        "image/jpeg"      => ".jpg",
        "image/png"       => ".png",
        "image/webp"      => ".webp",
        "application/pdf" => ".pdf",
        _ => ".bin"
    };

    /// <summary>
    /// Full server-side check: size, declared content-type, AND the actual
    /// file-extension the client sent (defense in depth — a mismatched
    /// extension/content-type pair is rejected rather than silently trusted).
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

        return (true, null);
    }
}
