using EventOpsOracle.Application.Announcements.DTOs;
using EventOpsOracle.Application.Common;
using EventOpsOracle.Application.Interfaces;
using EventOpsOracle.Application.Notifications.Abstractions;
using EventOpsOracle.Application.Notifications.Contracts;
using EventOpsOracle.Domain.Entities;
using EventOpsOracle.Domain.Enums;
using EventOpsOracle.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EventOpsOracle.Application.Announcements.Commands;

/// <summary>
/// Admin/Manager broadcasts a rich-text notification to an event's vendors
/// and/or crew.
///
/// The DB row remains the source of truth, so the message is always readable
/// later on the dashboard even if every outbound channel is down.
///
/// Outbound delivery is QUEUED, not sent inline. It used to loop over recipients
/// calling WhatsApp one at a time while the admin's request waited: a 200-person
/// event meant 200 sequential HTTP calls inside the request, any transient failure
/// was logged and lost with no retry, and the admin sat watching a spinner to find
/// out how many got through. Handing the recipients to the notification platform
/// instead makes the broadcast return immediately and gives every message the
/// outbox worker's retry, backoff, per-channel routing and delivery tracking.
///
/// The SignalR push stays inline: it is the in-page badge/refresh signal, it costs
/// nothing, and it is meaningless if delayed.
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
    private readonly INotificationDispatcher _notifications;
    private readonly AppUrlOptions _appUrls;
    private readonly ILogger<SendEventAnnouncementHandler> _logger;

    public SendEventAnnouncementHandler(
        IAppDbContext db,
        INotificationPusher push,
        INotificationDispatcher notifications,
        IOptions<AppUrlOptions> appUrls,
        ILogger<SendEventAnnouncementHandler> logger)
    {
        _db = db; _push = push; _notifications = notifications; _appUrls = appUrls.Value; _logger = logger;
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

        }

        // Queue the durable copy for every recipient. No mobile-number check here any
        // more: the platform resolves channels per recipient, so somebody with no
        // mobile still gets the inbox row (and email once that is their preference),
        // where the old loop simply skipped them.
        _notifications.Enqueue(recipients.Select(user => new NotificationRequest(
            NotificationTemplateCodes.EventAnnouncement,
            RecipientUserId: user.Id,
            // One key per recipient per announcement. The announcement id is already
            // unique per send, so a retried request cannot double-broadcast.
            BusinessEventKey: $"announcement:{announcement.Id}:{user.Id}",
            Data: new Dictionary<string, string?>
            {
                [NotificationTokens.Subject] = announcement.Subject,
                // The whole body goes in one token on purpose. EVENT_ANNOUNCEMENT's
                // stored template is "{{Subject}}" / "{{Message}}", and the seeder
                // never rewrites an existing template row -- so adding {{Link}} or
                // {{EventName}} to the catalogue would change nothing in production
                // and the link would silently vanish from live messages.
                [NotificationTokens.Message] = BuildAnnouncementBody(
                    user.FullName, ev.Title, announcement, attachmentCount, link)
            },
            ActorUserId: req.SentByUserId)));

        // RecipientCount is exact. The second number is now "queued for outbound
        // delivery", not "confirmed sent by WhatsApp" -- the send happens later in the
        // outbox worker, and true per-channel status lives on the delivery rows. The
        // UI wording was changed to match; claiming "sent" here would be a guess.
        announcement.RecordDelivery(recipients.Count, recipients.Count);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Announcement {AnnouncementId} stored for event {EventId} and queued for {Recipients} recipient(s), {Attachments} attachment(s).",
            announcement.Id, req.EventId, recipients.Count, attachmentCount);

        return Result.Success(new SendAnnouncementResultDto(
            announcement.Id, recipients.Count, recipients.Count, attachmentCount));
    }

    private string BuildLink(Guid announcementId)
    {
        var baseUrl = !string.IsNullOrWhiteSpace(_appUrls.BaseUrl)
            ? _appUrls.BaseUrl
            : (Environment.GetEnvironmentVariable("APP_BASE_URL") ?? "https://eventwos.app");
        return $"{baseUrl.TrimEnd('/')}/notifications?id={announcementId}";
    }

    /// <summary>
    /// Flattens the announcement into one plain-text body for the outbound channels.
    /// The rich formatting and the attachments themselves stay behind the deep link,
    /// which is what keeps attachments as links rather than files pushed over the
    /// wire to every recipient.
    /// </summary>
    private static string BuildAnnouncementBody(
        string fullName, string eventTitle, EventAnnouncement announcement, int attachmentCount, string link)
    {
        var attachmentNote = attachmentCount switch
        {
            0 => string.Empty,
            1 => "\n\n1 attachment - open the link to view it.",
            _ => $"\n\n{attachmentCount} attachments - open the link to view them."
        };

        return $"Hi {fullName}, update for {eventTitle}\n\n" +
               $"{announcement.Subject}\n{announcement.PlainTextPreview(300)}" +
               $"{attachmentNote}\n\nView full notification: {link}";
    }
}
