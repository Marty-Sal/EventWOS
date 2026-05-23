namespace EventWOS.Application.Auth.Interfaces;

/// <summary>OTP generation, hashing, and SMS dispatch abstraction.</summary>
public interface IOtpService
{
    /// <summary>Generates a 6-digit numeric OTP. Returns (plaintext, bcryptHash).</summary>
    (string Plaintext, string Hash) GenerateOtp();

    /// <summary>Verifies plaintext OTP against a stored BCrypt hash.</summary>
    bool VerifyOtp(string plaintext, string storedHash);

    /// <summary>Sends the OTP via configured SMS provider.</summary>
    Task<bool> SendOtpAsync(string mobile, string otp, CancellationToken cancellationToken = default);
}
