using EventOpsOracle.Application.Common;
using EventOpsOracle.Application.Interfaces;
using EventOpsOracle.Domain.Entities;
using EventOpsOracle.Domain.Enums;
using EventOpsOracle.Domain.Interfaces;
using EventOpsOracle.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EventOpsOracle.Application.Files.Queries;

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

        // Vendors approve their own crew, so resolve that relationship before
        // deciding. Only asked when the cheap checks have already failed and the
        // document is a crew registration document, so the common paths (owner,
        // Admin/Manager) still cost zero extra queries.
        var isOwningVendor = false;
        if (req.RequestingUserId != doc.OwnerId
            && !req.RequesterCanManageOthers
            && FileAccessPolicy.IsCrewRegistrationDocument(doc.DocumentType))
        {
            isOwningVendor = await IsOwningVendorAsync(req.RequestingUserId, doc.OwnerId, ct);
        }

        if (!FileAccessPolicy.CanDownload(doc.DocumentType, doc.OwnerId, req.RequestingUserId,
                req.RequesterCanManageOthers, req.RequesterCanReadIdentity, isOwningVendor))
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

    /// <summary>
    /// True when <paramref name="requesterId"/> is a Vendor and the file's owner
    /// is a Crew user tied to them: already in their crew (VendorId), or pending
    /// approval having registered with their referral code. Mirrors exactly how
    /// GetApprovalQueueHandler scopes crew rows to a vendor, so a vendor can open
    /// precisely the documents their own queue shows them and nothing else.
    /// </summary>
    private async Task<bool> IsOwningVendorAsync(Guid requesterId, Guid ownerId, CancellationToken ct)
    {
        var requester = await _db.Users
            .Where(u => u.Id == requesterId && !u.IsDeleted)
            .Select(u => new { u.Role, u.ReferralCode })
            .FirstOrDefaultAsync(ct);
        if (requester is null || requester.Role != UserRole.Vendor) return false;

        var owner = await _db.Users
            .Where(u => u.Id == ownerId && !u.IsDeleted)
            .Select(u => new { u.Role, u.VendorId, u.ReferralCodeUsed })
            .FirstOrDefaultAsync(ct);
        if (owner is null || owner.Role != UserRole.Crew) return false;

        if (owner.VendorId == requesterId) return true;

        return !string.IsNullOrEmpty(requester.ReferralCode)
            && owner.ReferralCodeUsed == requester.ReferralCode;
    }
}
