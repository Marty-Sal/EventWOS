using EventWOS.Application.Announcements.DTOs;
using EventWOS.Application.Common;
using EventWOS.Application.Interfaces;
using EventWOS.Domain.Entities;
using EventWOS.Domain.Enums;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EventWOS.Application.Announcements.Commands;

/// <summary>
/// Admin/Manager broadcasts a rich-text notification to an event's vendors
/// and/or crew.
///
/// Delivery is three-pronged and deliberately best-effort on the outbound
/// channels: the DB row is the source of truth (so the message is always
/// readable later on the dashboard/event screen even if WhatsApp is down),
/// while the SignalR push and the WhatsApp fan-out are fire-and-forget
/// per recipient — one bad mobile number must not fail the whole broadcast.
/// </summary>
public sealed record SendEventAnnouncementCommand(
    Guid   EventId,
    AnnouncementAudience Audience,
    string Subject,
    string BodyHtml,
    IReadOnlyList<Guid> AttachmentFileIds,
    Guid   SentByUserId
) : IRequest<Result<SendAnnouncementResultDto>>;

public sealed class SendEventAnnouncementHandler
    : IRequestHandler<SendEventAnnouncementCommand, Result<SendAnnouncementResultDto>>
{
    private readonly IAppDbContext _db;
    private readonly INotificationPusher _push;
    private readonly IWhatsAppProvider _whatsApp;
    private readonly AppUrlOptions _appUrls;
    private readonly ILogger<SendEventAnnouncementHandler> _logger;

    public SendEventAnnouncementHandler(
        IAppDbContext db,
        INotificationPusher push,
        IWhatsAppProvider whatsApp,
        IOptions<AppUrlOptions> appUrls,
        ILogger<SendEventAnnouncementHandler> logger)
    {
        _db = db; _push = push; _whatsApp = whatsApp; _appUrls = appUrls.Value; _logger = logger;
    }

    public async Task<Result<SendAnnouncementResultDto>> Handle(
        SendEventAnnouncementCommand req, CancellationToken ct)
    {
        var ev = await _db.Events.FirstOrDefaultAsync(e => e.Id == req.EventId && !e.IsDeleted, ct);
        if (ev is null)
            return Result.Failure<SendAnnouncementResultDto>(Error.NotFound);

        EventAnnouncement announcement;
        try
        {
            announcement = new EventAnnouncement(
                req.EventId, req.Audience, req.Subject, req.BodyHtml, req.SentByUserId);
        }
        catch (ArgumentException ex)
        {
            return Result.Failure<SendAnnouncementResultDto>(
                Error.Custom("Announcement.Invalid", ex.Message));
        }

        _db.EventAnnouncements.Add(announcement);

        // Only attach files that actually exist and were uploaded against
        // this event — stops a caller from stapling somebody else's document
        // (e.g. a crew member's ID proof) onto a broadcast.
        var attachmentCount = 0;
        if (req.AttachmentFileIds.Count > 0)
        {
            var validFileIds = await _db.FileDocuments
                .Where(f => req.AttachmentFileIds.Contains(f.Id)
                         && !f.IsDeleted
                         && f.DocumentType == DocumentType.EventDocument
                         && f.EntityId == req.EventId)
                .Select(f => f.Id)
                .ToListAsync(ct);

            foreach (var fileId in validFileIds)
                _db.EventAnnouncementAttachments.Add(new EventAnnouncementAttachment(announcement.Id, fileId));

            attachmentCount = validFileIds.Count;

            if (validFileIds.Count != req.AttachmentFileIds.Count)
            {
                _logger.LogWarning(
                    "Announcement {AnnouncementId}: {Rejected} attachment id(s) ignored (not an EventDocument for event {EventId}).",
                    announcement.Id, req.AttachmentFileIds.Count - validFileIds.Count, req.EventId);
            }
        }

        var recipientIds = await AnnouncementAccess.ResolveRecipientIdsAsync(_db, req.EventId, req.Audience, ct);

        var recipients = recipientIds.Count == 0
            ? new List<User>()
            : await _db.Users
                .Where(u => recipientIds.Contains(u.Id) && !u.IsDeleted && u.Status == UserStatus.Active)
                .ToListAsync(ct);

        var link = BuildLink(announcement.Id);
        var whatsAppSent = 0;

        foreach (var user in recipients)
        {
            try
            {
                await _push.PushToUserAsync(user.Id, "EventAnnouncement", new
                {
                    announcementId = announcement.Id,
                    eventId        = ev.Id,
                    eventTitle     = ev.Title,
                    subject        = announcement.Subject,
                    preview        = announcement.PlainTextPreview(140),
                    attachments    = attachmentCount,
                    link
                }, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Announcement {AnnouncementId}: real-time push failed for user {UserId}.",
                    announcement.Id, user.Id);
            }

            if (string.IsNullOrWhiteSpace(user.Mobile)) continue;

            try
            {
                var msg = BuildWhatsAppMessage(user.FullName, ev.Title, announcement, attachmentCount, link);
                if (await _whatsApp.SendAsync(user.Mobile, msg, ct)) whatsAppSent++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Announcement {AnnouncementId}: WhatsApp failed for user {UserId}.",
                    announcement.Id, user.Id);
            }
        }

        announcement.RecordDelivery(recipients.Count, whatsAppSent);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Announcement {AnnouncementId} sent for event {EventId} to {Recipients} recipient(s), {WhatsApp} via WhatsApp, {Attachments} attachment(s).",
            announcement.Id, req.EventId, recipients.Count, whatsAppSent, attachmentCount);

        return Result.Success(new SendAnnouncementResultDto(
            announcement.Id, recipients.Count, whatsAppSent, attachmentCount));
    }

    private string BuildLink(Guid announcementId)
    {
        var baseUrl = !string.IsNullOrWhiteSpace(_appUrls.BaseUrl)
            ? _appUrls.BaseUrl
            : (Environment.GetEnvironmentVariable("APP_BASE_URL") ?? "https://eventwos.app");
        return $"{baseUrl.TrimEnd('/')}/notifications?id={announcementId}";
    }

    /// <summary>
    /// WhatsApp is plain text, so the HTML body is flattened to a preview and
    /// the real content (formatting + attachment links) lives behind the
    /// deep link — which is also what keeps attachments as links rather than
    /// files pushed over the wire.
    /// </summary>
    private static string BuildWhatsAppMessage(
        string fullName, string eventTitle, EventAnnouncement announcement, int attachmentCount, string link)
    {
        var attachmentNote = attachmentCount switch
        {
            0 => string.Empty,
            1 => "\n\n📎 1 attachment — open the link to view it.",
            _ => $"\n\n📎 {attachmentCount} attachments — open the link to view them."
        };

        return $"Hi {fullName}, update for *{eventTitle}*\n\n" +
               $"*{announcement.Subject}*\n{announcement.PlainTextPreview(300)}" +
               $"{attachmentNote}\n\nView full notification: {link}";
    }
}
