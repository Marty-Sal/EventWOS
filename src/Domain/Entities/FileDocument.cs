using EventWOS.Domain.Common;
using EventWOS.Domain.Enums;

namespace EventWOS.Domain.Entities;

/// <summary>
/// Metadata-only record for a file stored in object storage (or local disk
/// in dev). PostgreSQL NEVER holds the file bytes — only this row, which
/// points at the bytes via <see cref="StorageKey"/>.
///
/// Field-name note: the spec's "UploadedAt"/"UploadedBy" map onto
/// BaseEntity's existing CreatedAt/CreatedBy (every other entity in this
/// codebase follows that convention — adding parallel columns would just
/// duplicate the same data under a different name).
/// </summary>
public sealed class FileDocument : BaseEntity
{
    private FileDocument() { }

    public FileDocument(
        Guid ownerId,
        Guid? entityId,
        DocumentType documentType,
        string storageKey,
        string originalFileName,
        string contentType,
        long fileSizeBytes,
        string fileHash,
        StorageProvider provider)
    {
        OwnerId          = ownerId;
        EntityId         = entityId;
        DocumentType     = documentType;
        StorageKey       = storageKey;
        OriginalFileName = originalFileName;
        ContentType      = contentType;
        FileSizeBytes    = fileSizeBytes;
        FileHash         = fileHash;
        Provider         = provider;
    }

    /// <summary>The user this file "belongs to" (e.g. the Crew member for a profile photo / ID proof).</summary>
    public Guid OwnerId { get; private set; }

    /// <summary>Optional related record — e.g. an Event id for EventDocument. Null for user-scoped docs.</summary>
    public Guid? EntityId { get; private set; }

    public DocumentType DocumentType { get; private set; }

    /// <summary>
    /// Opaque backend key, e.g. "crew/{ownerId}/identity/{fileId}.jpg".
    /// Never derived from the client-supplied filename — see
    /// FileStorageKeyBuilder. This is the ONLY thing that lets us locate
    /// the bytes; there is deliberately no permanent public URL column.
    /// </summary>
    public string StorageKey { get; private set; } = default!;

    /// <summary>Original client filename — display only, never used for storage paths or execution.</summary>
    public string OriginalFileName { get; private set; } = default!;
    public string ContentType   { get; private set; } = default!;
    public long   FileSizeBytes { get; private set; }

    /// <summary>SHA-256 hex digest of the stored bytes — integrity check + cheap de-dupe signal.</summary>
    public string FileHash { get; private set; } = default!;

    public StorageProvider Provider { get; private set; }

    /// <summary>Set only for image types that get a generated thumbnail (currently CrewProfilePhoto).</summary>
    public string? ThumbnailStorageKey { get; private set; }

    public void SetThumbnail(string thumbnailStorageKey) => ThumbnailStorageKey = thumbnailStorageKey;
}
