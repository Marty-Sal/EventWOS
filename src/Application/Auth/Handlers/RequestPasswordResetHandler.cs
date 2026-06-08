using EventWOS.Application.Auth.Commands;
using EventWOS.Application.Auth.Interfaces;
using EventWOS.Application.Common;
using EventWOS.Application.Interfaces;
using EventWOS.Domain.Entities;
using EventWOS.Domain.Enums;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EventWOS.Application.Auth.Handlers;

/// <summary>
/// Step 1 of forgot-password. Resolves user by username/email/mobile,
/// generates an OTP, stores the hashed OTP in OtpRequests, and dispatches
/// it via SMS to their registered mobile (always) plus email (if available).
///
/// SECURITY: never reveals whether the account exists. If no match we
/// still return success with a generic masked destination — the OTP
/// just isn't actually sent. This blocks account-enumeration attacks
/// via the forgot-password endpoint.
/// </summary>
public sealed class RequestPasswordResetHandler : IRequestHandler<RequestPasswordResetCommand, Result<RequestPasswordResetResponse>>
{
    private readonly IAppDbContext _db;
    private readonly IOtpService _otpService;
    private readonly IEmailService _email;
    private readonly IUnitOfWork _uow;
    private readonly IAuditLogger _audit;
    private readonly ILogger<RequestPasswordResetHandler> _logger;

    public RequestPasswordResetHandler(
        IAppDbContext db, IOtpService otpService, IEmailService email,
        IUnitOfWork uow, IAuditLogger audit, ILogger<RequestPasswordResetHandler> logger)
    {
        _db = db; _otpService = otpService; _email = email;
        _uow = uow; _audit = audit; _logger = logger;
    }

    public async Task<Result<RequestPasswordResetResponse>> Handle(RequestPasswordResetCommand req, CancellationToken ct)
    {
        var key = req.UsernameEmailOrMobile.Trim();
        var keyLower = key.ToLowerInvariant();

        var user = await _db.Users
            .FirstOrDefaultAsync(u => !u.IsDeleted
                                   && u.Status == UserStatus.Active
                                   && (u.Username == keyLower
                                    || u.Email    == keyLower
                                    || u.Mobile   == key), ct);

        // No user → return generic success. We DO NOT differentiate.
        if (user is null)
        {
            _logger.LogInformation("Password reset requested for non-existent key {Key}", key);
            return Result.Success(new RequestPasswordResetResponse(null, Mask(key)));
        }

        var (plaintext, hash) = _otpService.GenerateOtp();
        var otpRequest = new OtpRequest(user.Mobile, hash, deviceId: null, ipAddress: req.IpAddress);
        _db.OtpRequests.Add(otpRequest);
        await _uow.SaveChangesAsync(ct);

        // SMS — same channel the login OTP uses.
        await _otpService.SendOtpAsync(user.Mobile, plaintext, ct);
        // Email — best-effort, fire and forget result.
        if (!string.IsNullOrEmpty(user.Email))
            await _email.SendPasswordResetOtpEmailAsync(user.Email, user.FullName, plaintext, ct);

        await _audit.LogAsync(AuditAction.OtpRequested, nameof(User), user.Id.ToString(),
            additionalData: "Reason:PasswordReset", cancellationToken: ct);

        return Result.Success(new RequestPasswordResetResponse(
            otpRequest.Id, Mask(user.Mobile)));
    }

    /// <summary>Best-effort masking for the toast: "+91*****7890" / "j***@x.com".</summary>
    private static string Mask(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "your registered contact";
        if (s.Contains('@'))
        {
            var at = s.IndexOf('@');
            var prefix = at <= 1 ? "*" : s[0] + new string('*', at - 1);
            return prefix + s[at..];
        }
        return s.Length <= 4 ? new string('*', s.Length) : new string('*', s.Length - 4) + s[^4..];
    }
}
