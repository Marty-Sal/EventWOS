using EventOpsOracle.Domain.Common;
using EventOpsOracle.Domain.Enums;

namespace EventOpsOracle.Domain.Entities;

/// <summary>
/// A notification an Admin/Manager broadcasts to the vendors and/or crew of
/// one Event, composed in the rich-text editor on the Event view screen.
///
/// Persisted (unlike the transient SignalR pushes used elsewhere in this
/// codebase) for two reasons the spec calls out explicitly: recipients must
/// still find the message on their dashboard later, and anyone who opens the
/// event afterwards — including people assigned after the fact — must be
/// able to read the full announcement history together with its attachments.
///
/// Attachments are NOT stored here. They're ordinary <see cref="FileDocument"/>
/// rows (object storage + metadata, never bytes in Postgres) joined through
/// <see cref="EventAnnouncementAttachment"/>, and are always surfaced to
/// recipients as click-through links rather than embedded/attached files.
/// </summary>
public sealed class EventAnnouncement : BaseEntity
{
    private EventAnnouncement() { }

    public EventAnnouncement(
        Guid eventId,
        AnnouncementAudience audience,
        string subject,
        string bodyHtml,
        Guid sentByUserId)
    {
        if (eventId == Guid.Empty)
            throw new ArgumentException("EventId is required.", nameof(eventId));

        EventId  = eventId;
        Audience = audience;
        SetSubject(subject);
        SetBody(bodyHtml);
        CreatedBy = sentByUserId;
    }

    public Guid EventId { get; private set; }

    public AnnouncementAudience Audience { get; private set; }

    /// <summary>Short plain-text headline — what recipients see in their notification list and in the WhatsApp message.</summary>
    public string Subject { get; private set; } = default!;

    /// <summary>Rich-text HTML from the WYSIWYG editor (same editor as Settings → Terms &amp; Conditions).</summary>
    public string BodyHtml { get; private set; } = default!;

    /// <summary>How many users the audience resolved to at send time — display only ("Sent to 12 recipients").</summary>
    public int RecipientCount { get; private set; }

    /// <summary>How many of those recipients we successfully handed to the WhatsApp provider.</summary>
    public int WhatsAppSentCount { get; private set; }

    public void RecordDelivery(int recipientCount, int whatsAppSentCount)
    {
        RecipientCount    = recipientCount < 0 ? 0 : recipientCount;
        WhatsAppSentCount = whatsAppSentCount < 0 ? 0 : whatsAppSentCount;
    }

    // Same control-character scrub as TermsAndConditions.SetContent — admin
    // rich text is routinely pasted out of Word, which carries invisible C0
    // control chars, and Postgres' `text` type rejects embedded NUL outright.
    private static readonly System.Text.RegularExpressions.Regex ControlCharPattern =
        new(@"[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]", System.Text.RegularExpressions.RegexOptions.Compiled);

    private void SetSubject(string subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
            throw new ArgumentException("Subject is required.", nameof(subject));
        var cleaned = ControlCharPattern.Replace(subject, string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(cleaned))
            throw new ArgumentException("Subject is required.", nameof(subject));
        if (cleaned.Length > 200)
            throw new ArgumentException("Subject must be 200 characters or fewer.", nameof(subject));
        Subject = cleaned;
    }

    private void SetBody(string bodyHtml)
    {
        if (string.IsNullOrWhiteSpace(bodyHtml))
            throw new ArgumentException("Message body is required.", nameof(bodyHtml));
        var cleaned = ControlCharPattern.Replace(bodyHtml, string.Empty).Trim();
        // Quill leaves an empty paragraph behind when the user clears the
        // editor, so "<p><br></p>" has to count as empty here.
        var stripped = System.Text.RegularExpressions.Regex.Replace(cleaned, "<.*?>", string.Empty)
            .Replace("&nbsp;", " ").Trim();
        if (string.IsNullOrWhiteSpace(stripped))
            throw new ArgumentException("Message body is required.", nameof(bodyHtml));
        if (cleaned.Length > 200000)
            throw new ArgumentException("Message body must be 200,000 characters or fewer.", nameof(bodyHtml));
        BodyHtml = cleaned;
    }

    /// <summary>Plain-text preview for WhatsApp / list rows — tags stripped, collapsed whitespace, truncated.</summary>
    public string PlainTextPreview(int maxLength = 300)
    {
        var text = System.Text.RegularExpressions.Regex.Replace(BodyHtml, "<(br|/p|/div|/li)>", " ",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, "<.*?>", string.Empty);
        text = System.Net.WebUtility.HtmlDecode(text);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length <= maxLength ? text : text[..maxLength].TrimEnd() + "…";
    }
}
