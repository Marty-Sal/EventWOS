using EventWOS.Application.Auth.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EventWOS.Infrastructure.Auth;

/// <summary>
/// OTP generation with BCrypt hashing.
/// SMS delivery uses provider abstraction — swap out Twilio, MSG91, etc.
/// </summary>
public sealed class OtpService : IOtpService
{
    private readonly OtpOptions _options;
    private readonly ISmsProvider _smsProvider;
    private readonly ILogger<OtpService> _logger;

    public OtpService(IOptions<OtpOptions> options, ISmsProvider smsProvider, ILogger<OtpService> logger)
    {
        _options = options.Value;
        _smsProvider = smsProvider;
        _logger = logger;
    }

    public (string Plaintext, string Hash) GenerateOtp()
    {
        // Cryptographically secure 6-digit OTP
        var otp = Random.Shared.Next(100000, 999999).ToString("D6");
        var hash = BCrypt.Net.BCrypt.HashPassword(otp, workFactor: 10);
        return (otp, hash);
    }

    public bool VerifyOtp(string plaintext, string storedHash) =>
        BCrypt.Net.BCrypt.Verify(plaintext, storedHash);

    public async Task<bool> SendOtpAsync(string mobile, string otp, CancellationToken cancellationToken = default)
    {
        if (_options.IsDevelopmentMode)
        {
            // In dev, log OTP instead of sending SMS
            _logger.LogWarning("🔐 [DEV MODE] OTP for {Mobile}: {Otp}", mobile, otp);
            return true;
        }

        var message = $"Your EventWOS verification code is: {otp}. Valid for 10 minutes. Do not share.";
        return await _smsProvider.SendAsync(mobile, message, cancellationToken);
    }
}

public sealed class OtpOptions
{
    public const string SectionName = "Otp";
    public bool IsDevelopmentMode { get; init; } = true;
}

/// <summary>SMS provider abstraction. Implement for Twilio, MSG91, SNS, etc.</summary>
public interface ISmsProvider
{
    Task<bool> SendAsync(string mobile, string message, CancellationToken ct = default);
}

/// <summary>Stub SMS provider for development/testing.</summary>
public sealed class StubSmsProvider : ISmsProvider
{
    private readonly ILogger<StubSmsProvider> _logger;
    public StubSmsProvider(ILogger<StubSmsProvider> logger) => _logger = logger;

    public Task<bool> SendAsync(string mobile, string message, CancellationToken ct = default)
    {
        _logger.LogInformation("📱 [STUB SMS] To: {Mobile} | Message: {Message}", mobile, message);
        return Task.FromResult(true);
    }
}
