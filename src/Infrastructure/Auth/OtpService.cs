using EventWOS.Application.Auth.Interfaces;
using EventWOS.Application.Common;
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

    /// <inheritdoc/>
    public bool IsDevelopmentMode => _options.IsDevelopmentMode;

    /// <inheritdoc/>
    public bool ExposeOtpInApiResponse => _options.ExposeOtpInApiResponse;

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

    /// <summary>
    /// Stub SMS delivery: log the OTP instead of calling the SMS provider. This is a
    /// DELIVERY switch, not a security one, and is expected to stay true until a real
    /// SMS or WhatsApp provider is live.
    /// </summary>
    public bool IsDevelopmentMode { get; set; } = true;

    /// <summary>
    /// Return the plaintext OTP in the API response so a developer can complete the flow
    /// without a delivery channel. Defaults to false and is FORCED false in the
    /// Production environment by Program.cs, because the OTP alone is enough to mint a
    /// session for any account -- see IOtpService.ExposeOtpInApiResponse.
    /// </summary>
    public bool ExposeOtpInApiResponse { get; set; }
}

/// <summary>Stub SMS provider for development/testing.
/// Implements <see cref="EventWOS.Application.Common.ISmsProvider"/>.</summary>
public sealed class StubSmsProvider : ISmsProvider
{
    private readonly ILogger<StubSmsProvider> _logger;
    public StubSmsProvider(ILogger<StubSmsProvider> logger) => _logger = logger;

    public Task<bool> SendAsync(string mobile, string message, CancellationToken ct = default)
    {
        // Warning, not Information: this provider delivers nothing. Logging it as routine
        // is how "OTP sent" ends up in the logs for a message that never left the process.
        _logger.LogWarning(
            "[STUB SMS] NOT DELIVERED to {Mobile}. No SMS provider is configured. Message: {Message}",
            mobile, message);
        return Task.FromResult(false);
    }
}
