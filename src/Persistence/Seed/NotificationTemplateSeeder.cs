using EventWOS.Application.Notifications.Contracts;
using EventWOS.Domain.Entities;
using EventWOS.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EventWOS.Persistence.Seed;

/// <summary>
/// Seeds default wording for every notification code. Without a template a
/// notification cannot render, so the platform ships with defaults rather than
/// requiring an operator to author 20-odd templates before the first message can
/// go out.
///
/// Idempotent by (code, channel, language) and INSERT-ONLY: it never overwrites
/// an existing row, because the whole point of templates living in the database
/// is that an admin can reword them -- a deploy that reset their edits would be
/// worse than no seeding at all.
///
/// WhatsApp templates are seeded INACTIVE on purpose. Meta only permits
/// pre-approved templates outside a 24-hour service window, so activating one
/// without setting its approved provider template name would produce confident
/// failures. Set provider_template_id and flip is_active once the template is
/// approved in AiSensy / Meta Business Manager.
/// </summary>
public sealed class NotificationTemplateSeeder
{
    private readonly AppDbContext _db;
    private readonly ILogger<NotificationTemplateSeeder> _logger;

    public NotificationTemplateSeeder(AppDbContext db, ILogger<NotificationTemplateSeeder> logger)
    {
        _db     = db;
        _logger = logger;
    }

    /// <param name="Title">Short headline: the in-app row and the email subject.</param>
    /// <param name="Line">One-sentence body, shared by all three channels.</param>
    private sealed record Defaults(string Title, string Line);

    private static readonly Dictionary<string, Defaults> Catalogue = new(StringComparer.OrdinalIgnoreCase)
    {
        [NotificationTemplateCodes.AccountApproved] =
            new("Account approved", "Hi {{RecipientName}}, your EventWOS account has been approved. You can sign in now."),
        [NotificationTemplateCodes.AccountRejected] =
            new("Account not approved", "Hi {{RecipientName}}, your EventWOS registration was not approved. Reason: {{Reason}}"),
        [NotificationTemplateCodes.AccountInvited] =
            new("You have been added to EventWOS", "Hi {{RecipientName}}, {{ActorName}} added you to EventWOS as {{Role}}. Set your password here: {{Link}}"),
        [NotificationTemplateCodes.RegistrationPendingApproval] =
            new("New registration awaiting your approval",
                "{{RecipientName}}, {{ActorName}} has registered as {{Role}} and is waiting for your approval. Review: {{Link}}"),
        [NotificationTemplateCodes.ProfileCompleted] =
            new("Profile completed", "{{RecipientName}}, {{ActorName}} has completed their profile."),
        [NotificationTemplateCodes.PasswordResetOtp] =
            new("Your password reset code", "Your EventWOS password reset code is {{Otp}}. It expires shortly. Do not share it with anyone."),

        [NotificationTemplateCodes.VendorEventInvited] =
            new("New event invitation", "Hi {{RecipientName}}, you have been invited to {{EventName}} on {{EventDate}} at {{VenueName}}."),
        [NotificationTemplateCodes.VendorInviteRevoked] =
            new("Event invitation withdrawn", "Hi {{RecipientName}}, your invitation to {{EventName}} has been withdrawn."),
        [NotificationTemplateCodes.VendorEventReminder] =
            new("Event reminder", "Reminder: {{EventName}} is on {{EventDate}} at {{VenueName}}."),

        [NotificationTemplateCodes.CrewInvitation] =
            new("New work invitation", "Hi {{RecipientName}}, {{VendorName}} has invited you to work {{EventName}} on {{EventDate}} at {{VenueName}}."),
        [NotificationTemplateCodes.CrewAssignment] =
            new("You are assigned", "Hi {{RecipientName}}, you are assigned to {{EventName}} on {{EventDate}} at {{VenueName}}. Shift: {{ShiftName}}."),
        [NotificationTemplateCodes.CrewAssignmentApproved] =
            new("Assignment approved", "Good news {{RecipientName}} -- your assignment for {{EventName}} on {{EventDate}} is confirmed."),
        [NotificationTemplateCodes.CrewAssignmentRejected] =
            new("Assignment not approved", "Hi {{RecipientName}}, your assignment for {{EventName}} was not approved. Reason: {{Reason}}"),
        [NotificationTemplateCodes.AssignmentPendingApproval] =
            new("Crew awaiting your approval",
                "{{RecipientName}}, {{CrewName}} was approved by {{VendorName}} for {{EventName}} on {{EventDate}} and needs your final approval. Review: {{Link}}"),
        [NotificationTemplateCodes.CrewAcceptedAssignment] =
            new("{{CrewName}} accepted",
                "{{RecipientName}}, {{CrewName}} accepted {{EventName}} on {{EventDate}} and is waiting for you to forward them for approval: {{Link}}"),
        [NotificationTemplateCodes.CrewDeclinedAssignment] =
            new("{{CrewName}} declined",
                "{{RecipientName}}, {{CrewName}} declined {{EventName}} on {{EventDate}}. Reason: {{Reason}}. You will need to fill the slot: {{Link}}"),
        [NotificationTemplateCodes.CrewInviteRevoked] =
            new("Invitation withdrawn", "Hi {{RecipientName}}, your invitation for {{EventName}} has been withdrawn."),
        [NotificationTemplateCodes.CrewAssignmentReminder] =
            new("Upcoming shift", "Reminder {{RecipientName}}: {{EventName}} on {{EventDate}} at {{VenueName}}. Shift: {{ShiftName}}."),

        [NotificationTemplateCodes.EventAnnouncement] =
            new("{{Subject}}", "{{Message}}"),
        [NotificationTemplateCodes.EventUpdated] =
            new("Event details changed", "{{EventName}} has been updated. New details: {{EventDate}} at {{VenueName}}."),
        [NotificationTemplateCodes.EventCancelled] =
            new("Event cancelled", "Important: {{EventName}} on {{EventDate}} has been CANCELLED. Please do not travel to the venue."),
        [NotificationTemplateCodes.EventStarting] =
            new("Event starting soon", "{{EventName}} starts {{EventTime}} at {{VenueName}}."),
        [NotificationTemplateCodes.ShiftChanged] =
            new("Shift changed", "Your shift for {{EventName}} has changed to {{ShiftName}} on {{EventDate}}."),

        [NotificationTemplateCodes.AttendanceReminder] =
            new("Check in reminder", "{{RecipientName}}, please check in for {{EventName}} at {{VenueName}}."),
        [NotificationTemplateCodes.CheckInVerified] =
            new("Check in verified", "Your check in for {{EventName}} has been verified."),

        [NotificationTemplateCodes.PaymentApproved] =
            new("Payment approved", "Hi {{RecipientName}}, your payment of {{Amount}} for {{EventName}} has been approved."),
        [NotificationTemplateCodes.PaymentRejected] =
            new("Payment query", "Hi {{RecipientName}}, your payment for {{EventName}} needs attention. Reason: {{Reason}}"),
        [NotificationTemplateCodes.PayrollReleased] =
            new("Payment released", "Hi {{RecipientName}}, {{Amount}} for {{EventName}} has been released."),
    };

    public async Task SeedAsync(CancellationToken ct = default)
    {
        var existing = await _db.NotificationTemplates
            .Select(t => new { t.Code, t.Channel, t.Language })
            .ToListAsync(ct);

        var have = existing
            .Select(e => (e.Code.ToUpperInvariant(), e.Channel))
            .ToHashSet();

        var inserted = 0;

        foreach (var (code, defaults) in Catalogue)
        {
            var upper = code.ToUpperInvariant();

            if (!have.Contains((upper, NotificationChannel.InApp)))
            {
                _db.NotificationTemplates.Add(
                    new NotificationTemplate(code, NotificationChannel.InApp, defaults.Line, defaults.Title));
                inserted++;
            }

            if (!have.Contains((upper, NotificationChannel.Email)))
            {
                _db.NotificationTemplates.Add(
                    new NotificationTemplate(code, NotificationChannel.Email, EmailBody(defaults), defaults.Title));
                inserted++;
            }

            if (!have.Contains((upper, NotificationChannel.WhatsApp)))
            {
                var whatsApp = new NotificationTemplate(code, NotificationChannel.WhatsApp, defaults.Line);
                // Inactive until an approved provider template name is set --
                // see the class remarks.
                whatsApp.Deactivate(DateTime.UtcNow);
                _db.NotificationTemplates.Add(whatsApp);
                inserted++;
            }
        }

        if (inserted == 0)
        {
            _logger.LogInformation("Notification templates already seeded; nothing to insert.");
            return;
        }

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "Seeded {Count} notification template(s). WhatsApp templates are inactive until provider template names are configured.",
            inserted);
    }

    /// <summary>
    /// Plain, deliberately boring HTML. Values are HTML-encoded by the renderer,
    /// so the markup here is the only markup in the message.
    /// </summary>
    private static string EmailBody(Defaults defaults) =>
        $"""
        <div style="font-family:Segoe UI,Helvetica,Arial,sans-serif;font-size:15px;color:#111827;line-height:1.6">
          <p style="margin:0 0 16px 0;font-size:18px;font-weight:600">{defaults.Title}</p>
          <p style="margin:0 0 16px 0">{defaults.Line}</p>
          <p style="margin:24px 0 0 0;font-size:13px;color:#6b7280">This is an automated message from EventWOS.</p>
        </div>
        """;
}
