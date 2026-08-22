using EventWOS.Application.Announcements.DTOs;
using EventWOS.Application.Interfaces;
using EventWOS.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Announcements;

/// <summary>
/// Hydrates announcement rows into DTOs: attachment metadata, sender name and
/// the caller's read state, all batched so the list endpoints stay at a fixed
/// number of queries regardless of how many announcements come back.
/// </summary>
internal static class AnnouncementDtoBuilder
{
    public static Task<IReadOnlyList<EventAnnouncementDto>> BuildAsync(
        IAppDbContext db,
        IReadOnlyList<EventAnnouncement> announcements,
        string eventTitle,
        DateTime eventStartAt,
        Guid requestingUserId,
        CancellationToken ct)
        => BuildAsync(db, announcements,
            _ => (eventTitle, eventStartAt), requestingUserId, ct);

    public static async Task<IReadOnlyList<EventAnnouncementDto>> BuildAsync(
        IAppDbContext db,
        IReadOnlyList<EventAnnouncement> announcements,
        Func<Guid, (string Title, DateTime StartAt)> eventLookup,
        Guid requestingUserId,
        CancellationToken ct)
    {
        var ids = announcements.Select(a => a.Id).ToList();

        var links = await db.EventAnnouncementAttachments
            .Where(x => ids.Contains(x.AnnouncementId) && !x.IsDeleted)
            .Select(x => new { x.AnnouncementId, x.FileDocumentId })
            .ToListAsync(ct);

        var fileIds = links.Select(l => l.FileDocumentId).Distinct().ToList();
        var files = fileIds.Count == 0
            ? new List<FileDocument>()
            : await db.FileDocuments
                .Where(f => fileIds.Contains(f.Id) && !f.IsDeleted)
                .ToListAsync(ct);
        var filesById = files.ToDictionary(f => f.Id);

        var attachmentsByAnnouncement = links
            .Where(l => filesById.ContainsKey(l.FileDocumentId))
            .GroupBy(l => l.AnnouncementId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<AnnouncementAttachmentDto>)g
                    .Select(l => filesById[l.FileDocumentId])
                    .OrderBy(f => f.CreatedAt)
                    .Select(f => new AnnouncementAttachmentDto(
                        f.Id, f.OriginalFileName, f.ContentType, f.FileSizeBytes))
                    .ToList());

        var senderIds = announcements
            .Where(a => a.CreatedBy.HasValue)
            .Select(a => a.CreatedBy!.Value)
            .Distinct()
            .ToList();
        var senderNames = senderIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.Users
                .Where(u => senderIds.Contains(u.Id))
                .Select(u => new { u.Id, u.FullName })
                .ToDictionaryAsync(u => u.Id, u => u.FullName, ct);

        var readIds = await db.EventAnnouncementReads
            .Where(r => ids.Contains(r.AnnouncementId) && r.UserId == requestingUserId && !r.IsDeleted)
            .Select(r => r.AnnouncementId)
            .ToListAsync(ct);
        var readSet = readIds.ToHashSet();

        return announcements.Select(a =>
        {
            var (title, startAt) = eventLookup(a.EventId);
            return new EventAnnouncementDto(
                a.Id,
                a.EventId,
                title,
                startAt,
                a.Audience,
                a.Subject,
                a.BodyHtml,
                a.CreatedBy.HasValue && senderNames.TryGetValue(a.CreatedBy.Value, out var name) ? name : "System",
                a.CreatedAt,
                a.RecipientCount,
                a.WhatsAppSentCount,
                readSet.Contains(a.Id),
                attachmentsByAnnouncement.GetValueOrDefault(a.Id, Array.Empty<AnnouncementAttachmentDto>()));
        }).ToList();
    }
}
