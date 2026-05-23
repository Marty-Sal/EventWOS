using EventWOS.Domain.Common;
using EventWOS.Domain.Enums;

namespace EventWOS.Domain.Entities;

/// <summary>
/// Tracks OTP generation and verification lifecycle.
/// OTP is stored as BCrypt hash — never in plaintext.
/// </summary>
public sealed class OtpRequest : BaseEntity
{
    private OtpRequest() { }

    public OtpRequest(string mobile, string hashedOtp, string? deviceId, string? ipAddress)
    {
        Mobile = mobile;
        HashedOtp = hashedOtp;
        DeviceId = deviceId;
        IpAddress = ipAddress;
        ExpiresAt = DateTime.UtcNow.AddMinutes(10);
        Status = OtpStatus.Pending;
        AttemptCount = 0;
    }

    public string Mobile { get; private set; } = default!;
    public string HashedOtp { get; private set; } = default!;
    public string? DeviceId { get; private set; }
    public string? IpAddress { get; private set; }
    public DateTime ExpiresAt { get; private set; }
    public OtpStatus Status { get; private set; }
    public int AttemptCount { get; private set; }
    public DateTime? VerifiedAt { get; private set; }

    public bool IsExpired => DateTime.UtcNow > ExpiresAt;
    public bool IsValid => Status == OtpStatus.Pending && !IsExpired;
    public bool MaxAttemptsReached => AttemptCount >= 3;

    public void MarkVerified()
    {
        Status = OtpStatus.Verified;
        VerifiedAt = DateTime.UtcNow;
    }

    public void MarkFailed()
    {
        AttemptCount++;
        if (AttemptCount >= 3 || IsExpired)
            Status = OtpStatus.Failed;
    }

    public void MarkExpired() => Status = OtpStatus.Expired;
}
