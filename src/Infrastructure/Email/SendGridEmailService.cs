using System.Net.Http.Json;
using System.Text.Json;
using EventWOS.Application.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace EventWOS.Infrastructure.Email;

/// <summary>
/// SendGrid v3 transactional email sender. Activated automatically when
/// SENDGRID_API_KEY is present in configuration; otherwise StubEmailService
/// is registered instead. Talks directly to the v3 REST API
/// (https://api.sendgrid.com/v3/mail/send) so we don't take the heavyweight
/// SendGrid SDK as a dependency.
///
/// Failures (auth, rate limit, etc.) are logged and return false — never
/// throw, so a transient email outage doesn't bring down user-facing flows
/// like approval. Caller decides whether to retry / queue.
/// </summary>
public sealed class SendGridEmailService : IEmailService
{
    private readonly HttpClient _http;
    private readonly ILogger<SendGridEmailService> _logger;
    private readonly string _apiKey;
    private readonly string _fromEmail;
    private readonly string _fromName;

    public SendGridEmailService(IConfiguration cfg, HttpClient http, ILogger<SendGridEmailService> logger)
    {
        _http      = http;
        _logger    = logger;
        _apiKey    = cfg["SendGrid:ApiKey"]    ?? cfg["SENDGRID_API_KEY"]    ?? throw new InvalidOperationException("SendGrid:ApiKey missing.");
        _fromEmail = cfg["SendGrid:FromEmail"] ?? cfg["SENDGRID_FROM_EMAIL"] ?? throw new InvalidOperationException("SendGrid:FromEmail missing.");
        _fromName  = cfg["SendGrid:FromName"]  ?? "EventWOS";

        _http.BaseAddress = new Uri("https://api.sendgrid.com/");
        _http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
    }

    public async Task<bool> SendAsync(string toEmail, string subject, string htmlBody,
        string? plainTextBody = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(toEmail))
        {
            _logger.LogWarning("SendGrid send skipped — empty recipient.");
            return false;
        }

        var payload = new
        {
            personalizations = new[] { new { to = new[] { new { email = toEmail } } } },
            from             = new { email = _fromEmail, name = _fromName },
            subject,
            content = plainTextBody is null
                ? new[] { new { type = "text/html",  value = htmlBody } }
                : new[]
                  {
                      new { type = "text/plain", value = plainTextBody },
                      new { type = "text/html",  value = htmlBody }
                  }
        };

        try
        {
            using var resp = await _http.PostAsJsonAsync("v3/mail/send", payload, ct);
            if (resp.IsSuccessStatusCode) return true;

            var body = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogError("SendGrid send failed: {Status} {Body}", resp.StatusCode, body);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SendGrid request threw an exception.");
            return false;
        }
    }

    public Task<bool> SendApprovalEmailAsync(string toEmail, string fullName, string role,
        string? referralCode, string loginUrl, CancellationToken ct = default)
    {
        var subject = $"Your EventWOS {role} account is approved 🎉";
        var referralBlock = string.IsNullOrEmpty(referralCode) ? "" :
            $"<p style='margin:16px 0;padding:12px;background:#f5f3ff;border-radius:8px'>" +
            $"Your <strong>referral code</strong> is <code style='font-size:16px'>{referralCode}</code>. " +
            $"Share <a href='{loginUrl.Replace("/login", "/register/crew?ref=" + referralCode)}'>this link</a> " +
            $"with your crew to onboard them.</p>";

        var html = $@"
<div style='font-family:Inter,Arial,sans-serif;max-width:560px;margin:auto;color:#1f2937'>
  <h2 style='color:#4f46e5'>Welcome aboard, {fullName}!</h2>
  <p>Your EventWOS {role} account has been approved. You can now sign in and start using the platform.</p>
  {referralBlock}
  <p style='margin-top:24px'>
    <a href='{loginUrl}' style='background:#4f46e5;color:white;padding:12px 24px;border-radius:8px;text-decoration:none;display:inline-block'>Sign in</a>
  </p>
  <p style='color:#6b7280;font-size:12px;margin-top:32px'>If you didn't expect this email, please ignore it.</p>
</div>";
        var plain = $"Welcome, {fullName}! Your EventWOS {role} account is approved. Sign in: {loginUrl}";
        return SendAsync(toEmail, subject, html, plain, ct);
    }

    public Task<bool> SendRejectionEmailAsync(string toEmail, string fullName, string reason,
        DateTime canRetryAt, CancellationToken ct = default)
    {
        var subject = "Your EventWOS registration was not approved";
        var html = $@"
<div style='font-family:Inter,Arial,sans-serif;max-width:560px;margin:auto;color:#1f2937'>
  <h2 style='color:#dc2626'>Hi {fullName},</h2>
  <p>Unfortunately, your EventWOS registration was not approved at this time.</p>
  <p style='padding:12px;background:#fef2f2;border-left:3px solid #dc2626;border-radius:4px'>
    <strong>Reason:</strong> {reason}
  </p>
  <p>You're welcome to try registering again after <strong>{canRetryAt:dd MMM yyyy, HH:mm} UTC</strong>.</p>
  <p style='color:#6b7280;font-size:12px;margin-top:32px'>Questions? Reply to this email.</p>
</div>";
        var plain = $"Hi {fullName}, your EventWOS registration was not approved. Reason: {reason}. You can retry after {canRetryAt:u}.";
        return SendAsync(toEmail, subject, html, plain, ct);
    }

    public Task<bool> SendPasswordResetOtpEmailAsync(string toEmail, string fullName, string otp, CancellationToken ct = default)
    {
        var subject = "Your EventWOS password reset code";
        var html = $@"
<div style='font-family:Inter,Arial,sans-serif;max-width:560px;margin:auto;color:#1f2937'>
  <h2 style='color:#4f46e5'>Hi {fullName},</h2>
  <p>Use the code below to reset your EventWOS password. It expires in 10 minutes.</p>
  <p style='font-size:32px;font-weight:bold;letter-spacing:6px;padding:16px;background:#f3f4f6;border-radius:8px;text-align:center'>{otp}</p>
  <p style='color:#6b7280;font-size:12px;margin-top:32px'>If you didn't request this, you can safely ignore the email.</p>
</div>";
        var plain = $"Your EventWOS password reset code: {otp} (valid for 10 minutes).";
        return SendAsync(toEmail, subject, html, plain, ct);
    }
}
