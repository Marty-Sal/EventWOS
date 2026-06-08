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
}
