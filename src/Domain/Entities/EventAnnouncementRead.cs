using EventWOS.Domain.Common;

namespace EventWOS.Domain.Entities;

/// <summary>
/// Marks that a specific user has opened a specific announcement — drives the
/// unread badge on the crew/vendor dashboard. Absence of a row means unread,
/// so nothing needs backfilling for announcements sent before a user joined.
/// </summary>
public sealed class EventAnnouncementRead : BaseEntity
{
    private EventAnnouncementRead() { }

    public EventAnnouncementRead(Guid announcementId, Guid userId)
    {
        if (announcementId == Guid.Empty)
            throw new ArgumentException("AnnouncementId is required.", nameof(announcementId));
        if (userId == Guid.Empty)
            throw new ArgumentException("UserId is required.", nameof(userId));

        AnnouncementId = announcementId;
        UserId         = userId;
        ReadAt         = DateTime.UtcNow;
    }

    public Guid     AnnouncementId { get; private set; }
    public Guid     UserId         { get; private set; }
    public DateTime ReadAt         { get; private set; }
}
