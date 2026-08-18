using EventWOS.Application.Common;
using EventWOS.Application.Files.DTOs;
using EventWOS.Application.Interfaces;
using EventWOS.Domain.Entities;
using EventWOS.Domain.Enums;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Result;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;

namespace EventWOS.Application.Files.Commands;

/// <summary>
/// Uploads a file: validate → optimize (images only) → store bytes via
/// IFileStorage → persist metadata. The Application layer never touches a
/// concrete disk path or cloud SDK — only IFileStorage/IImageProcessor.
/// </summary>
public sealed record UploadFileCommand(
    Guid RequestingUserId,
    bool RequesterCanManageOthers,
    Guid OwnerId,
    Guid? EntityId,
    DocumentType DocumentType,
    byte[] Content,
    string OriginalFileName,
    string ContentType
) : IRequest<Result<FileDocumentDto>>;

public sealed class UploadFileValidator : AbstractValidator<UploadFileCommand>
{
    public UploadFileValidator()
    {
        RuleFor(x => x.OriginalFileName).NotEmpty().MaximumLength(255);
        RuleFor(x => x.ContentType).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Content).NotNull();
        RuleFor(x => x)
            .Must(x => FileValidationPolicy.Validate(x.DocumentType, x.Content.LongLength, x.ContentType, x.OriginalFileName, x.Content).IsValid)
            .WithMessage(x => FileValidationPolicy.Validate(x.DocumentType, x.Content.LongLength, x.ContentType, x.OriginalFileName, x.Content).Error
                              ?? "File failed validation.");
    }
}

public sealed class UploadFileHandler : IRequestHandler<UploadFileCommand, Result<FileDocumentDto>>
{
    private readonly IAppDbContext _db;
    private readonly IFileStorage _storage;
    private readonly IImageProcessor _imageProcessor;
    private readonly IUnitOfWork _uow;
    private readonly IAuditLogger _audit;
    private readonly ILogger<UploadFileHandler> _logger;

    public UploadFileHandler(
        IAppDbContext db, IFileStorage storage, IImageProcessor imageProcessor,
        IUnitOfWork uow, IAuditLogger audit, ILogger<UploadFileHandler> logger)
    {
        _db = db; _storage = storage; _imageProcessor = imageProcessor;
        _uow = uow; _audit = audit; _logger = logger;
    }

    public async Task<Result<FileDocumentDto>> Handle(UploadFileCommand req, CancellationToken ct)
    {
        // Defence in depth — FluentValidation already ran this, but never trust a single layer.
        // Includes the magic-byte signature check (content, not just the declared Content-Type/extension).
        var (isValid, error) = FileValidationPolicy.Validate(req.DocumentType, req.Content.LongLength, req.ContentType, req.OriginalFileName, req.Content);
        if (!isValid)
            return Result.Failure<FileDocumentDto>(Error.Custom("Files.InvalidFile", error!));

        if (!FileAccessPolicy.CanUploadFor(req.OwnerId, req.RequestingUserId, req.RequesterCanManageOthers))
            return Result.Failure<FileDocumentDto>(Error.Unauthorized);

        var fileId    = Guid.NewGuid();
        var extension = FileValidationPolicy.ExtensionForContentType(req.ContentType);
        var storageKey = FileStorageKeyBuilder.Build(req.DocumentType, req.OwnerId, req.EntityId, fileId, extension);

        await using var sourceStream = new MemoryStream(req.Content);
        string? thumbnailKey = null;
        string contentTypeToStore = req.ContentType;

        try
        {
            if (FileValidationPolicy.IsImage(req.DocumentType))
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
                await _storage.UploadAsync(storageKey, sourceStream, req.ContentType, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "File upload to storage failed for key {StorageKey}", storageKey);
            return Result.Failure<FileDocumentDto>(Error.Custom("Files.StorageError", "Could not store the file. Please try again."));
        }

        var hash = Convert.ToHexString(SHA256.HashData(req.Content));

        var doc = new FileDocument(
            ownerId: req.OwnerId, entityId: req.EntityId, documentType: req.DocumentType,
            storageKey: storageKey, originalFileName: SanitizeFileName(req.OriginalFileName),
            contentType: contentTypeToStore, fileSizeBytes: req.Content.LongLength, fileHash: hash,
            provider: _storage.ActiveProvider);
        if (thumbnailKey is not null) doc.SetThumbnail(thumbnailKey);

        _db.FileDocuments.Add(doc);
        await _uow.SaveChangesAsync(ct);

        await _audit.LogAsync(AuditAction.FileUploaded, nameof(FileDocument), doc.Id.ToString(),
            newValues: new { req.DocumentType, req.OwnerId, req.OriginalFileName, req.ContentType, Size = req.Content.LongLength },
            actorUserId: req.RequestingUserId, cancellationToken: ct);

        _logger.LogInformation("File {FileId} ({Type}) uploaded for owner {OwnerId} by {Actor}",
            doc.Id, req.DocumentType, req.OwnerId, req.RequestingUserId);

        return Result.Success(new FileDocumentDto(
            doc.Id, doc.OwnerId, doc.EntityId, doc.DocumentType, doc.OriginalFileName,
            doc.ContentType, doc.FileSizeBytes, doc.ThumbnailStorageKey is not null, doc.CreatedAt));
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
