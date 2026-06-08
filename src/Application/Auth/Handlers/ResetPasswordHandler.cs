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
/// Step 2 of forgot-password. Verifies the OTP, then SetPassword on
/// the user. Does NOT issue a JWT — caller is bounced back to the
/// login page with a "Password updated, please sign in" toast. This
/// keeps the password-reset and session-creation surfaces separated,
/// which makes the audit story cleaner and rules out chained CSRF.
/// </summary>
public sealed class ResetPasswordHandler : IRequestHandler<ResetPasswordCommand, Result>
{
    private readonly IAppDbContext _db;
    private readonly IOtpService _otp;
    private readonly IPasswordHasher _hasher;
    private readonly IUnitOfWork _uow;
    private readonly IAuditLogger _audit;
    private readonly ILogger<ResetPasswordHandler> _logger;

    public ResetPasswordHandler(
        IAppDbContext db, IOtpService otp, IPasswordHasher hasher,
        IUnitOfWork uow, IAuditLogger audit, ILogger<ResetPasswordHandler> logger)
    {
        _db = db; _otp = otp; _hasher = hasher;
        _uow = uow; _audit = audit; _logger = logger;
    }

    public async Task<Result> Handle(ResetPasswordCommand req, CancellationToken ct)
    {
        if (!PasswordRules.IsValid(req.NewPassword))
            return Result.Failure(Error.Custom("Auth.WeakPassword", PasswordRules.Description));

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

        user.SetPassword(_hasher.Hash(req.NewPassword));
        await _uow.SaveChangesAsync(ct);
        await _audit.LogAsync(AuditAction.PasswordChanged, nameof(User), user.Id.ToString(),
            additionalData: "Reason:Reset", cancellationToken: ct);
        _logger.LogInformation("Password reset for user {UserId}.", user.Id);
        return Result.Success();
    }
}
