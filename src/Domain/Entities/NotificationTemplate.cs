using EventWOS.Domain.Common;
using EventWOS.Domain.Enums;

namespace EventWOS.Domain.Entities;

/// <summary>
/// The wording of a notification, per channel, kept out of business code. A
/// handler says "notify CREW_ASSIGNMENT"; what the crew member actually reads
/// is decided here and can be reworded without a deployment.
///
/// One row per (Code, Channel, Language). WhatsApp templates additionally carry
/// <see cref="ProviderTemplateId"/>, because Meta-approved template names live
/// at the provider and we can only pass parameters into them -- we cannot send
/// arbitrary WhatsApp text to a user outside their 24-hour service window.
/// </summary>
public sealed class NotificationTemplate : BaseEntity
{
    private NotificationTemplate() { }

    public NotificationTemplate(
        string code,
        NotificationChannel channel,
        string body,
        string? subject = null,
        string language = "en",
        string? providerTemplateId = null)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("Code is required.", nameof(code));
        if (string.IsNullOrWhiteSpace(body))
            throw new ArgumentException("Body is required.", nameof(body));

        Code               = code.Trim().ToUpperInvariant();
        Channel            = channel;
        Body               = body;
        Subject            = subject;
        Language           = string.IsNullOrWhiteSpace(language) ? "en" : language.Trim().ToLowerInvariant();
        ProviderTemplateId = providerTemplateId;
        Version            = 1;
        IsActive           = true;
    }

    /// <summary>Stable business code, e.g. CREW_ASSIGNMENT.</summary>
    public string Code { get; private set; } = default!;

    public NotificationChannel Channel { get; private set; }

    /// <summary>BCP-47-ish language tag. "en" today; the column exists so Hindi can be added without a migration.</summary>
    public string Language { get; private set; } = "en";

    /// <summary>Email subject line. Null for channels that have no subject.</summary>
    public string? Subject { get; private set; }

    /// <summary>
    /// Body with {{Placeholder}} tokens. Substitution only -- no expressions, no
    /// loops, no code. Anything richer would turn an admin-editable text field
    /// into a template-injection surface.
    /// </summary>
    public string Body { get; private set; } = default!;

    /// <summary>The provider-side template/campaign name (AiSensy campaign, Meta template) when the channel requires one.</summary>
    public string? ProviderTemplateId { get; private set; }

    /// <summary>Bumped on every content change and stamped onto deliveries, so history shows the wording actually sent.</summary>
    public int Version { get; private set; }

    /// <summary>Inactive templates are skipped during channel selection, which is how a channel gets switched off per notification type.</summary>
    public bool IsActive { get; private set; }

    public void UpdateContent(string body, string? subject, string? providerTemplateId, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(body))
            throw new ArgumentException("Body is required.", nameof(body));

        Body               = body;
        Subject            = subject;
        ProviderTemplateId = providerTemplateId;
        Version           += 1;
        UpdatedAt          = nowUtc;
    }

    public void Activate(DateTime nowUtc)   { IsActive = true;  UpdatedAt = nowUtc; }
    public void Deactivate(DateTime nowUtc) { IsActive = false; UpdatedAt = nowUtc; }
}
