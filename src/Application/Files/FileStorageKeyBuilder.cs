using EventWOS.Domain.Enums;

namespace EventWOS.Application.Files;

/// <summary>
/// Mints opaque storage keys. Never uses the client-supplied filename —
/// always a fresh Guid — so keys can't collide, leak PII, or be used to
/// traverse/guess other users' files.
/// </summary>
public static class FileStorageKeyBuilder
{
    public static string Build(DocumentType type, Guid ownerId, Guid? entityId, Guid fileId, string extension)
        => type switch
        {
            DocumentType.CrewProfilePhoto        => $"crew/{ownerId}/profile/{fileId}{extension}",
            DocumentType.CrewIdentificationProof => $"crew/{ownerId}/identity/{fileId}{extension}",
            DocumentType.VendorDocument          => $"vendor/{ownerId}/documents/{fileId}{extension}",
            DocumentType.EventDocument           => $"events/{entityId ?? ownerId}/documents/{fileId}{extension}",
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown document type.")
        };

    /// <summary>Derives a sibling key for a generated thumbnail: ".../{fileId}.jpg" → ".../{fileId}-thumb.jpg".</summary>
    public static string ThumbnailKey(string originalKey)
    {
        var dot = originalKey.LastIndexOf('.');
        return dot < 0 ? originalKey + "-thumb" : originalKey.Insert(dot, "-thumb");
    }
}
