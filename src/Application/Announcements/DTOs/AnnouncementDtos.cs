using EventOpsOracle.Domain.Enums;

namespace EventOpsOracle.Application.Announcements.DTOs;

/// <summary>
/// One attachment on an announcement. Deliberately carries only metadata —
/// recipients get a click-through LINK (the download endpoint), never the
/// bytes inline, per spec.
/// </summary>
public sealed record AnnouncementAttachmentDto(
    Guid   FileId,
    string FileName,
    string ContentType,
    long   FileSizeBytes
)
{
    /// <summary>True for content a browser can render in a tab (images/PDF) rather than only download.</summary>
    public bool IsViewableInline =>
        ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
        ContentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase);
}

/// <summary>An event notification as shown in the event's history and on a recipient's dashboard.</summary>
public sealed record EventAnnouncementDto(
    Guid   Id,
    Guid   EventId,
    string EventTitle,
    DateTime EventStartAt,
    AnnouncementAudience Audience,
    string Subject,
    string BodyHtml,
    string SentByName,
    DateTime SentAt,
    int    RecipientCount,
    int    WhatsAppSentCount,
    bool   IsRead,
    IReadOnlyList<AnnouncementAttachmentDto> Attachments
);

/// <summary>Result of sending — lets the UI report "sent to N recipients (M via WhatsApp)".</summary>
public sealed record SendAnnouncementResultDto(
    Guid AnnouncementId,
    int  RecipientCount,
    int  WhatsAppSentCount,
    int  AttachmentCount
);
