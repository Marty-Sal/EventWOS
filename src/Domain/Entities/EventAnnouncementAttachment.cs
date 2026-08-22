using EventWOS.Domain.Common;

namespace EventWOS.Domain.Entities;

/// <summary>
/// Join row: one file attached to one <see cref="EventAnnouncement"/>.
///
/// A separate table (rather than re-pointing <see cref="FileDocument.EntityId"/>
/// at the announcement) because the files are uploaded BEFORE the
/// announcement row exists — the composer uploads as you pick files, tagged
/// to the event — and their event association is worth keeping intact.
/// </summary>
public sealed class EventAnnouncementAttachment : BaseEntity
{
    private EventAnnouncementAttachment() { }

    public EventAnnouncementAttachment(Guid announcementId, Guid fileDocumentId)
    {
        if (announcementId == Guid.Empty)
            throw new ArgumentException("AnnouncementId is required.", nameof(announcementId));
        if (fileDocumentId == Guid.Empty)
            throw new ArgumentException("FileDocumentId is required.", nameof(fileDocumentId));

        AnnouncementId = announcementId;
        FileDocumentId = fileDocumentId;
    }

    public Guid AnnouncementId { get; private set; }
    public Guid FileDocumentId { get; private set; }
}
