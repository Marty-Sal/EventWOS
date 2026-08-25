using EventOpsOracle.Application.Announcements.DTOs;
using EventOpsOracle.Application.Common;
using EventOpsOracle.Application.Interfaces;
using EventOpsOracle.Domain.Enums;
using EventOpsOracle.Domain.Interfaces;
using EventOpsOracle.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EventOpsOracle.Application.Announcements.Queries;

/// <summary>
/// Streams an announcement attachment to a recipient.
///
/// This exists as its own path because the generic FileAccessPolicy is
/// owner-or-admin: the file's owner is the Admin who uploaded it, so a crew
/// member would be denied. Authorization here is instead "is the caller
/// allowed to read THIS announcement" — connected to the event and inside the
/// audience — which is the same rule the notification list uses, so a link in
/// a WhatsApp message can never open a file its recipient shouldn't see.
/// </summary>
public sealed record DownloadAnnouncementAttachmentQuery(
    Guid AnnouncementId,
    Guid FileId,
    Guid RequestingUserId,
    UserRole RequestingUserRole,
    bool IsPrivileged
) : IRequest<Result<AnnouncementAttachmentDownload>>;

public sealed record AnnouncementAttachmentDownload(Stream Content, string ContentType, string OriginalFileName);

public sealed class DownloadAnnouncementAttachmentHandler
    : IRequestHandler<DownloadAnnouncementAttachmentQuery, Result<AnnouncementAttachmentDownload>>
{
    private readonly IAppDbContext _db;
    private readonly IFileStorage _storage;
    private readonly ILogger<DownloadAnnouncementAttachmentHandler> _logger;

    public DownloadAnnouncementAttachmentHandler(
        IAppDbContext db, IFileStorage storage, ILogger<DownloadAnnouncementAttachmentHandler> logger)
    {
        _db = db; _storage = storage; _logger = logger;
    }

    public async Task<Result<AnnouncementAttachmentDownload>> Handle(
        DownloadAnnouncementAttachmentQuery req, CancellationToken ct)
    {
        var announcement = await _db.EventAnnouncements
            .FirstOrDefaultAsync(a => a.Id == req.AnnouncementId && !a.IsDeleted, ct);
        if (announcement is null)
            return Result.Failure<AnnouncementAttachmentDownload>(Error.NotFound);

        // The file must genuinely belong to this announcement — otherwise the
        // announcement id would be a way to launder access to any file id.
        var linked = await _db.EventAnnouncementAttachments
            .AnyAsync(x => x.AnnouncementId == req.AnnouncementId && x.FileDocumentId == req.FileId && !x.IsDeleted, ct);
        if (!linked)
            return Result.Failure<AnnouncementAttachmentDownload>(Error.NotFound);

        if (!req.IsPrivileged)
        {
            if (!AnnouncementAccess.Includes(announcement.Audience, req.RequestingUserRole))
                return Result.Failure<AnnouncementAttachmentDownload>(Error.Unauthorized);

            var connected = await AnnouncementAccess.IsConnectedToEventAsync(
                _db, announcement.EventId, req.RequestingUserId, req.RequestingUserRole, ct);
            if (!connected)
            {
                _logger.LogWarning(
                    "Denied announcement attachment: announcement={AnnouncementId} file={FileId} user={UserId}",
                    req.AnnouncementId, req.FileId, req.RequestingUserId);
                return Result.Failure<AnnouncementAttachmentDownload>(Error.Unauthorized);
            }
        }

        var doc = await _db.FileDocuments.FirstOrDefaultAsync(f => f.Id == req.FileId && !f.IsDeleted, ct);
        if (doc is null)
            return Result.Failure<AnnouncementAttachmentDownload>(Error.NotFound);

        Stream content;
        try
        {
            content = await _storage.DownloadAsync(doc.StorageKey, ct);
        }
        catch (FileNotFoundException)
        {
            _logger.LogError("Announcement attachment {FileId} is missing from storage (key={Key}).", doc.Id, doc.StorageKey);
            return Result.Failure<AnnouncementAttachmentDownload>(
                Error.Custom("Files.MissingObject", "The attachment could not be found in storage."));
        }

        return Result.Success(new AnnouncementAttachmentDownload(content, doc.ContentType, doc.OriginalFileName));
    }
}
