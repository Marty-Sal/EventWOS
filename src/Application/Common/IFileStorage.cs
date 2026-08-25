namespace EventOpsOracle.Application.Common;

/// <summary>
/// Storage-backend abstraction. The Application/business layer depends on
/// ONLY this interface — never on a concrete disk path, S3 SDK type, or
/// Azure SDK type. Swapping LocalFileStorage for an ObjectStorage
/// implementation (S3, R2, MinIO, Azure Blob) is a one-line DI change in
/// Program.cs; no business/handler code changes.
///
/// Keys are opaque strings minted by FileStorageKeyBuilder
/// (e.g. "crew/{ownerId}/identity/{fileId}.jpg") — implementations must
/// treat them as an arbitrary path/object-key, never re-derive or trust a
/// client-supplied filename.
/// </summary>
public interface IFileStorage
{
    /// <summary>Which backend this implementation is (recorded per-FileDocument row so a provider migration doesn't break reads of older files).</summary>
    EventOpsOracle.Domain.Enums.StorageProvider ActiveProvider { get; }

    /// <summary>Uploads content under the given key, overwriting if it already exists. Returns the same key back for convenience/chaining.</summary>
    Task<string> UploadAsync(string storageKey, Stream content, string contentType, CancellationToken ct = default);

    /// <summary>Opens a readable stream for the object at the given key. Throws FileNotFoundException if it doesn't exist.</summary>
    Task<Stream> DownloadAsync(string storageKey, CancellationToken ct = default);

    /// <summary>Deletes the object at the given key. No-op (does not throw) if it doesn't exist.</summary>
    Task DeleteAsync(string storageKey, CancellationToken ct = default);

    Task<bool> ExistsAsync(string storageKey, CancellationToken ct = default);

    /// <summary>
    /// Optional short-lived pre-signed/SAS download URL for direct
    /// client-to-storage transfer, bypassing the API for the byte stream.
    /// Object-storage backends (S3/R2/MinIO/Azure) support this natively.
    /// LocalFileStorage returns null — callers must fall back to streaming
    /// the file through the API themselves (FilesController does this).
    /// </summary>
    Task<string?> GetPresignedDownloadUrlAsync(string storageKey, TimeSpan expiry, CancellationToken ct = default);
}
