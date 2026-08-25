namespace EventOpsOracle.Application.Auth.Interfaces;

/// <summary>OTP generation, hashing, and SMS dispatch abstraction.</summary>
public interface IOtpService
{
    /// <summary>Generates a 6-digit numeric OTP. Returns (plaintext, bcryptHash).</summary>
    (string Plaintext, string Hash) GenerateOtp();

    /// <summary>Verifies plaintext OTP against a stored BCrypt hash.</summary>
    bool VerifyOtp(string plaintext, string storedHash);

    /// <summary>Sends the OTP via configured SMS provider.</summary>
    Task<bool> SendOtpAsync(string mobile, string otp, CancellationToken cancellationToken = default);

    /// <summary>
    /// True when SMS delivery is stubbed: the OTP is written to the application log
    /// instead of being handed to the SMS provider. Safe to leave on in a deployed
    /// environment while no SMS provider is live -- it controls delivery only.
    /// </summary>
    bool IsDevelopmentMode { get; }

    /// <summary>
    /// True when the plaintext OTP may be returned in the API response body.
    ///
    /// DANGEROUS, and deliberately separate from <see cref="IsDevelopmentMode"/>. The OTP
    /// is the only thing standing between a caller who knows a mobile number and a signed
    /// in session as that user, because VerifyOtp mints an access token. Anything that
    /// returns it to the caller turns "knows your mobile number" into "is you".
    ///
    /// Forced off in the Production environment regardless of configuration. Get the OTP
    /// from the application log or the email copy instead.
    /// </summary>
    bool ExposeOtpInApiResponse { get; }
}
