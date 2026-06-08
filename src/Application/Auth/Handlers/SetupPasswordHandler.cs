using EventWOS.Application.Auth.Commands;
using EventWOS.Application.Auth.Interfaces;
using EventWOS.Application.Interfaces;
using EventWOS.Application.Registration.Validators;
using EventWOS.Domain.Entities;
using EventWOS.Domain.Enums;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EventWOS.Application.Auth.Handlers;

/// <summary>
/// First-login password + username setup for grandfathered users.
/// Verifies the OTP, sets the new username (must be unique) AND the
/// new password in one transaction, then clears RequirePasswordReset.
///
/// Why separate from ResetPasswordHandler? Audit clarity (reason=Setup
/// vs reason=Reset) and the additional username uniqueness check —
/// grandfathered users were backfilled with username = their mobile,
/// which most will want to change.
/// </summary>
public sealed class SetupPasswordHandler : IRequestHandler<SetupPasswordCommand, Result>
{
    private readonly IAppDbContext _db;
    private readonly IOtpService _otp;
    private readonly IPasswordHasher _hasher;
    private readonly IUnitOfWork _uow;
    private readonly IAuditLogger _audit;
    private readonly ILogger<SetupPasswordHandler> _logger;

    public SetupPasswordHandler(
        IAppDbContext db, IOtpService otp, IPasswordHasher hasher,
        IUnitOfWork uow, IAuditLogger audit, ILogger<SetupPasswordHandler> logger)
    {
        _db = db; _otp = otp; _hasher = hasher;
        _uow = uow; _audit = audit; _logger = logger;
    }

    public async Task<Result> Handle(SetupPasswordCommand req, CancellationToken ct)
    {
        if (!PasswordRules.IsValid(req.NewPassword))
            return Result.Failure(Error.Custom("Auth.WeakPassword", PasswordRules.Description));

        var newUsername = req.NewUsername.Trim().ToLowerInvariant();
        if (newUsername.Length is < 3 or > 50)
            return Result.Failure(Error.Custom("Auth.InvalidUsername", "Username must be 3–50 characters."));

        var otpRequest = await _db.OtpRequests
            .FirstOrDefaultAsync(o => o.Id == req.OtpRequestId && o.Mobile == req.Mobile, ct);
        if (otpRequest is null) return Result.Failure(Error.InvalidOtp);
        if (!otpRequest.IsValid) return Result.Failure(otpRequest.IsExpired ? Error.OtpExpired : Error.InvalidOtp);
        if (otpRequest.MaxAttemptsReached) return Result.Failure(Error.OtpMaxAttempts);
        if (!_otp.VerifyOtp(req.Otp, otpRequest.HashedOtp))
        {
            otpRequest.MarkFailed();
            await _uow.SaveChangesAsync(ct);
            return Result.Failure(Error.InvalidOtp);
        }
        otpRequest.MarkVerified();

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Mobile == req.Mobile && !u.IsDeleted, ct);
        if (user is null) return Result.Failure(Error.UserNotFound);

        // Username uniqueness — allow keeping the current one (= no-op).
        if (user.Username != newUsername)
        {
            var taken = await _db.Users.AnyAsync(u => u.Username == newUsername && u.Id != user.Id, ct);
            if (taken) return Result.Failure(Error.Custom("Registration.UsernameTaken", "That username is already taken."));
            user.SetUsername(newUsername);
        }
        user.SetPassword(_hasher.Hash(req.NewPassword));

        await _uow.SaveChangesAsync(ct);
        await _audit.LogAsync(AuditAction.PasswordChanged, nameof(User), user.Id.ToString(),
            additionalData: "Reason:FirstSetup", cancellationToken: ct);
        _logger.LogInformation("Password setup completed for grandfathered user {UserId}.", user.Id);
        return Result.Success();
    }
}
