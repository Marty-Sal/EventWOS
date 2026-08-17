using EventWOS.Application.Common;
using EventWOS.Application.Interfaces;
using EventWOS.Domain.Entities;
using EventWOS.Domain.Enums;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EventWOS.Application.Files.Queries;

public sealed record DownloadFileQuery(
    Guid FileId,
    Guid RequestingUserId,
    bool RequesterCanManageOthers,
    bool RequesterCanReadIdentity
) : IRequest<Result<FileDownloadResult>>;

public sealed record FileDownloadResult(Stream Content, string ContentType, string OriginalFileName, DocumentType DocumentType);

public sealed class DownloadFileHandler : IRequestHandler<DownloadFileQuery, Result<FileDownloadResult>>
{
    private readonly IAppDbContext _db;
    private readonly IFileStorage _storage;
    private readonly IAuditLogger _audit;
    private readonly ILogger<DownloadFileHandler> _logger;

    public DownloadFileHandler(IAppDbContext db, IFileStorage storage, IAuditLogger audit, ILogger<DownloadFileHandler> logger)
    {
        _db = db; _storage = storage; _audit = audit; _logger = logger;
    }

    public async Task<Result<FileDownloadResult>> Handle(DownloadFileQuery req, CancellationToken ct)
    {
        var doc = await _db.FileDocuments.FirstOrDefaultAsync(f => f.Id == req.FileId && !f.IsDeleted, ct);
        if (doc is null)
            return Result.Failure<FileDownloadResult>(Error.NotFound);

        if (!FileAccessPolicy.CanDownload(doc.DocumentType, doc.OwnerId, req.RequestingUserId,
                req.RequesterCanManageOthers, req.RequesterCanReadIdentity))
        {
            _logger.LogWarning("Denied file download: file={FileId} type={Type} owner={Owner} requester={Requester}",
                doc.Id, doc.DocumentType, doc.OwnerId, req.RequestingUserId);
            return Result.Failure<FileDownloadResult>(Error.Unauthorized);
        }

        // Policy: every access to a sensitive identity document is logged, including the owner viewing their own.
        if (FileValidationPolicy.IsSensitive(doc.DocumentType))
        {
            await _audit.LogAsync(AuditAction.SensitiveDocumentAccessed, nameof(FileDocument), doc.Id.ToString(),
                additionalData: $"Owner:{doc.OwnerId}", actorUserId: req.RequestingUserId, cancellationToken: ct);
        }

        Stream content;
        try
        {
            content = await _storage.DownloadAsync(doc.StorageKey, ct);
        }
        catch (FileNotFoundException)
        {
            _logger.LogError("FileDocument {FileId} has a metadata row but the object is missing from storage (key={Key})", doc.Id, doc.StorageKey);
            return Result.Failure<FileDownloadResult>(Error.Custom("Files.MissingObject", "The file could not be found in storage."));
        }

        return Result.Success(new FileDownloadResult(content, doc.ContentType, doc.OriginalFileName, doc.DocumentType));
    }
}
