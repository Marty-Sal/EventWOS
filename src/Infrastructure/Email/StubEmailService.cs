using EventWOS.Application.Common;
using Microsoft.Extensions.Logging;

namespace EventWOS.Infrastructure.Email;

/// <summary>
/// Dev-mode email "sender" — logs the message instead of dispatching.
/// Active whenever SENDGRID_API_KEY is missing from configuration so the
/// app boots fine without secrets. Same shape as StubSmsProvider so the
/// pattern is consistent across notification channels.
/// </summary>
public sealed class StubEmailService : IEmailService
{
    private readonly ILogger<StubEmailService> _logger;
    public StubEmailService(ILogger<StubEmailService> logger) => _logger = logger;

    public Task<bool> SendAsync(string toEmail, string subject, string htmlBody,
        string? plainTextBody = null, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "📧 [STUB EMAIL] To: {To} | Subject: {Subject}\n{Body}",
            toEmail, subject, plainTextBody ?? htmlBody);
        return Task.FromResult(true);
    }

    public Task<bool> SendApprovalEmailAsync(string toEmail, string fullName, string role,
        string? referralCode, string loginUrl, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "📧 [STUB EMAIL → APPROVAL] To: {To} | Name: {Name} | Role: {Role} | Referral: {Code} | Login: {Url}",
            toEmail, fullName, role, referralCode ?? "(n/a)", loginUrl);
        return Task.FromResult(true);
    }

    public Task<bool> SendRejectionEmailAsync(string toEmail, string fullName, string reason,
        DateTime canRetryAt, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "📧 [STUB EMAIL → REJECTION] To: {To} | Name: {Name} | Reason: {Reason} | Can retry after: {Retry:u}",
            toEmail, fullName, reason, canRetryAt);
        return Task.FromResult(true);
    }

    public Task<bool> SendPasswordResetOtpEmailAsync(string toEmail, string fullName, string otp, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "📧 [STUB EMAIL → RESET OTP] To: {To} | Name: {Name} | OTP: {Otp}",
            toEmail, fullName, otp);
        return Task.FromResult(true);
    }
}
