using EventWOS.Application.Common;
using EventWOS.Application.Interfaces;
using EventWOS.Domain.Entities;
using EventWOS.Domain.Enums;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EventWOS.Application.Files.Commands;

public sealed record DeleteFileCommand(
    Guid FileId, Guid RequestingUserId, bool RequesterCanManageOthers
) : IRequest<Result>;

public sealed class DeleteFileHandler : IRequestHandler<DeleteFileCommand, Result>
{
    private readonly IAppDbContext _db;
    private readonly IFileStorage _storage;
    private readonly IUnitOfWork _uow;
    private readonly IAuditLogger _audit;
    private readonly ILogger<DeleteFileHandler> _logger;

    public DeleteFileHandler(IAppDbContext db, IFileStorage storage, IUnitOfWork uow, IAuditLogger audit, ILogger<DeleteFileHandler> logger)
    {
        _db = db; _storage = storage; _uow = uow; _audit = audit; _logger = logger;
    }

    public async Task<Result> Handle(DeleteFileCommand req, CancellationToken ct)
    {
        var doc = await _db.FileDocuments.FirstOrDefaultAsync(f => f.Id == req.FileId && !f.IsDeleted, ct);
        if (doc is null) return Result.Failure(Error.NotFound);

        if (!FileAccessPolicy.CanDelete(doc.DocumentType, doc.OwnerId, req.RequestingUserId, req.RequesterCanManageOthers))
            return Result.Failure(Error.Unauthorized);

        try
        {
            await _storage.DeleteAsync(doc.StorageKey, ct);
            if (doc.ThumbnailStorageKey is not null)
                await _storage.DeleteAsync(doc.ThumbnailStorageKey, ct);
        }
        catch (Exception ex)
        {
            // Metadata soft-delete still proceeds — an orphaned object in storage is a cheap cleanup
            // job later; a stuck "can't delete" UX for the user is the worse failure mode.
            _logger.LogError(ex, "Failed to delete storage object for file {FileId} (key={Key}) — proceeding with metadata soft-delete anyway", doc.Id, doc.StorageKey);
        }

        doc.IsDeleted = true;
        doc.DeletedAt = DateTime.UtcNow;
        doc.DeletedBy = req.RequestingUserId;
        await _uow.SaveChangesAsync(ct);

        await _audit.LogAsync(AuditAction.FileDeleted, nameof(FileDocument), doc.Id.ToString(),
            oldValues: new { doc.DocumentType, doc.OwnerId, doc.OriginalFileName },
            actorUserId: req.RequestingUserId, cancellationToken: ct);

        return Result.Success();
    }
}
