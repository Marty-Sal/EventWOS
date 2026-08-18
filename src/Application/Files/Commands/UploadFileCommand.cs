using EventWOS.Application.Common;
using EventWOS.Application.Files.DTOs;
using EventWOS.Domain.Entities;
using EventWOS.Domain.Enums;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Result;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Logging;

namespace EventWOS.Application.Files.Commands;

/// <summary>
/// Uploads a file for an already-authenticated caller. The actual
/// validate/optimize/store/persist pipeline lives in IFileUploadStorer
/// (shared with RegisterCrewHandler's anonymous upload-during-signup path)
/// — this handler only adds the permission check and the SaveChanges/audit
/// transaction boundary around it.
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
    private readonly IFileUploadStorer _storer;
    private readonly IUnitOfWork _uow;
    private readonly IAuditLogger _audit;
    private readonly ILogger<UploadFileHandler> _logger;

    public UploadFileHandler(
        IFileUploadStorer storer,
        IUnitOfWork uow, IAuditLogger audit, ILogger<UploadFileHandler> logger)
    {
        _storer = storer; _uow = uow; _audit = audit; _logger = logger;
    }

    public async Task<Result<FileDocumentDto>> Handle(UploadFileCommand req, CancellationToken ct)
    {
        if (!FileAccessPolicy.CanUploadFor(req.OwnerId, req.RequestingUserId, req.RequesterCanManageOthers))
            return Result.Failure<FileDocumentDto>(Error.Unauthorized);

        var stored = await _storer.StoreAsync(
            req.OwnerId, req.EntityId, req.DocumentType, req.Content, req.OriginalFileName, req.ContentType, ct);
        if (stored.IsFailure)
            return Result.Failure<FileDocumentDto>(stored.Error);

        await _uow.SaveChangesAsync(ct);

        var doc = stored.Value;
        await _audit.LogAsync(AuditAction.FileUploaded, nameof(FileDocument), doc.Id.ToString(),
            newValues: new { req.DocumentType, req.OwnerId, req.OriginalFileName, req.ContentType, Size = req.Content.LongLength },
            actorUserId: req.RequestingUserId, cancellationToken: ct);

        _logger.LogInformation("File {FileId} ({Type}) uploaded for owner {OwnerId} by {Actor}",
            doc.Id, req.DocumentType, req.OwnerId, req.RequestingUserId);

        return Result.Success(new FileDocumentDto(
            doc.Id, doc.OwnerId, doc.EntityId, doc.DocumentType, doc.OriginalFileName,
            doc.ContentType, doc.FileSizeBytes, doc.ThumbnailStorageKey is not null, doc.CreatedAt));
    }
}
