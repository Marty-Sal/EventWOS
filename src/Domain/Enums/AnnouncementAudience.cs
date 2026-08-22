namespace EventWOS.Domain.Enums;

/// <summary>
/// Who an <see cref="Entities.EventAnnouncement"/> goes out to. The audience
/// is resolved at SEND time into concrete recipients (for WhatsApp fan-out
/// and the real-time push), but it is also stored on the row so that
/// visibility can be re-evaluated later — someone assigned to the event
/// AFTER the announcement was sent still sees it in the event's
/// notification history, which is exactly what the spec asks for.
/// </summary>
public enum AnnouncementAudience
{
    /// <summary>Vendors assigned to the event (via assignment or shift quota).</summary>
    Vendors = 1,

    /// <summary>Crew assigned to the event.</summary>
    Crew = 2,

    /// <summary>Both vendors and crew.</summary>
    Both = 3
}
