namespace EventWOS.Application.Common;

/// <summary>
/// SMS dispatch abstraction. Lives in Application so handlers can depend
/// on it directly (mirrors IEmailService). Infrastructure provides the
/// concrete implementation — StubSmsProvider for dev (logs) or a real
/// provider (Twilio, MSG91, etc.) in production.
/// </summary>
public interface ISmsProvider
{
    Task<bool> SendAsync(string mobile, string message, CancellationToken ct = default);
}
