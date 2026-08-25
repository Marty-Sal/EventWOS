using EventOpsOracle.Application.Common;
using EventOpsOracle.Application.Interfaces;
using EventOpsOracle.Domain.Entities;
using EventOpsOracle.Domain.Enums;
using EventOpsOracle.Shared.Result;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;

namespace EventOpsOracle.Application.Files;

/// <summary>
/// Shared "validate → optimize (images only) → upload bytes → build
/// FileDocument" pipeline, factored out of UploadFileHandler so it can also
/// be called from RegisterCrewHandler — Crew self-registration uploads a
/// profile photo / ID proof before an authenticated account (or an
/// OwnerId) exists, so it can't go through FilesController's [Authorize]
/// upload endpoint. Both callers get identical validation/signature/
/// optimization behaviour from one place.
///
/// Deliberately does NOT call SaveChangesAsync — it only adds the
/// FileDocument to the tracked DbContext. The caller controls the
/// transaction boundary, so e.g. RegisterCrewHandler can commit the new
/// User row and its FileDocument row(s) in one atomic SaveChangesAsync.
/// </summary>
public interface IFileUploadStorer
{
    Task<Result<FileDocument>> StoreAsync(
        Guid ownerId, Guid? entityId, DocumentType documentType,
        byte[] content, string originalFileName, string contentType,
        CancellationToken ct = default);
}

public sealed class FileUploadStorer : IFileUploadStorer
{
    private readonly IAppDbContext _db;
    private readonly IFileStorage _storage;
    private readonly IImageProcessor _imageProcessor;
    private readonly ILogger<FileUploadStorer> _logger;

    public FileUploadStorer(IAppDbContext db, IFileStorage storage, IImageProcessor imageProcessor, ILogger<FileUploadStorer> logger)
    {
        _db = db; _storage = storage; _imageProcessor = imageProcessor; _logger = logger;
    }

    public async Task<Result<FileDocument>> StoreAsync(
        Guid ownerId, Guid? entityId, DocumentType documentType,
        byte[] content, string originalFileName, string contentType,
        CancellationToken ct = default)
    {
        // Signature check included — never trust the declared Content-Type/extension alone.
        var (isValid, error) = FileValidationPolicy.Validate(documentType, content.LongLength, contentType, originalFileName, content);
        if (!isValid)
            return Result.Failure<FileDocument>(Error.Custom("Files.InvalidFile", error!));

        var fileId    = Guid.NewGuid();
        var extension = FileValidationPolicy.ExtensionForContentType(contentType);
        var storageKey = FileStorageKeyBuilder.Build(documentType, ownerId, entityId, fileId, extension);

        await using var sourceStream = new MemoryStream(content);
        string? thumbnailKey = null;
        string contentTypeToStore = contentType;

        try
        {
            if (FileValidationPolicy.IsImage(documentType))
            {
                var processed = await _imageProcessor.OptimizeAsync(sourceStream, ct);
                await using (processed.Optimized)
                {
                    await _storage.UploadAsync(storageKey, processed.Optimized, processed.OptimizedContentType, ct);
                }
                contentTypeToStore = processed.OptimizedContentType;

                if (processed.Thumbnail is not null)
                {
                    thumbnailKey = FileStorageKeyBuilder.ThumbnailKey(storageKey);
                    await using (processed.Thumbnail)
                    {
                        await _storage.UploadAsync(thumbnailKey, processed.Thumbnail, processed.ThumbnailContentType ?? contentTypeToStore, ct);
                    }
                }
            }
            else
            {
                await _storage.UploadAsync(storageKey, sourceStream, contentType, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "File upload to storage failed for key {StorageKey}", storageKey);
            return Result.Failure<FileDocument>(Error.Custom("Files.StorageError", "Could not store the file. Please try again."));
        }

        var hash = Convert.ToHexString(SHA256.HashData(content));

        var doc = new FileDocument(
            ownerId: ownerId, entityId: entityId, documentType: documentType,
            storageKey: storageKey, originalFileName: SanitizeFileName(originalFileName),
            contentType: contentTypeToStore, fileSizeBytes: content.LongLength, fileHash: hash,
            provider: _storage.ActiveProvider);
        if (thumbnailKey is not null) doc.SetThumbnail(thumbnailKey);

        _db.FileDocuments.Add(doc);
        return Result.Success(doc);
    }

    /// <summary>Strips path separators/control chars — this is display-only data but still never trusted raw.</summary>
    private static string SanitizeFileName(string name)
    {
        var cleaned = Path.GetFileName(name);
        foreach (var c in Path.GetInvalidFileNameChars())
            cleaned = cleaned.Replace(c, '_');
        return cleaned.Length > 255 ? cleaned[..255] : cleaned;
    }
}
