namespace EventWOS.Application.Common;

/// <summary>
/// Sends transactional email. Implementation chosen in Infrastructure —
/// StubEmailService (logs only) for dev, SendGridEmailService for prod
/// (activates when SENDGRID_API_KEY is configured). Handlers should call
/// the typed methods (SendApprovalEmailAsync etc.) rather than the
/// generic SendAsync, so templates stay in one place.
/// </summary>
public interface IEmailService
{
    Task<bool> SendAsync(
        string toEmail,
        string subject,
        string htmlBody,
        string? plainTextBody = null,
        CancellationToken ct = default);

    Task<bool> SendApprovalEmailAsync(
        string toEmail, string fullName, string role,
        string? referralCode, string loginUrl, CancellationToken ct = default);

    Task<bool> SendRejectionEmailAsync(
        string toEmail, string fullName, string reason,
        DateTime canRetryAt, CancellationToken ct = default);

    Task<bool> SendPasswordResetOtpEmailAsync(
        string toEmail, string fullName, string otp, CancellationToken ct = default);

    /// <summary>
    /// Sent when an Admin/Vendor directly adds a Crew/Vendor account
    /// (CreateVendorCommand/CreateCrewCommand) — skips the approval queue
    /// entirely (an authorized party already vouched for them) but they
    /// still need to set a password and fill in their profile. setupLink
    /// points at /setup-password?mobile=... — the existing first-login flow.
    /// </summary>
    Task<bool> SendAccountInviteEmailAsync(
        string toEmail, string fullName, string role, string invitedByName,
        string setupLink, CancellationToken ct = default);

    /// <summary>
    /// Sent to the Admin/Vendor who directly added an account, once that
    /// person finishes filling in their profile for the first time.
    /// </summary>
    Task<bool> SendProfileCompletedEmailAsync(
        string toEmail, string inviterName, string fullName, string role,
        CancellationToken ct = default);
}
