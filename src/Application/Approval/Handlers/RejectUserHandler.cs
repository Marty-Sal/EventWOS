using EventWOS.Application.Approval.Commands;
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

namespace EventWOS.Application.Approval.Handlers;

/// <summary>
/// Rejects a Pending self-registration. The 24-hour cool-down clock
/// starts at RejectedAt (set inside user.Reject()). The rejection
/// email tells the user exactly when they can retry.
/// </summary>
public sealed class RejectUserHandler : IRequestHandler<RejectUserCommand, Result>
{
    private readonly IAppDbContext _db;
    private readonly IUnitOfWork _uow;
    private readonly IAuditLogger _audit;
    private readonly IEmailService _email;
    private readonly ISmsProvider _sms;
    private readonly INotificationPusher _push;
    private readonly ICurrentUser _me;
    private readonly ILogger<RejectUserHandler> _logger;
    private static readonly TimeSpan CoolDown = TimeSpan.FromHours(24);

    public RejectUserHandler(
        IAppDbContext db, IUnitOfWork uow, IAuditLogger audit,
        IEmailService email, ISmsProvider sms, INotificationPusher push,
        ICurrentUser me,
        ILogger<RejectUserHandler> logger)
    {
        _db = db; _uow = uow; _audit = audit;
        _email = email; _sms = sms; _push = push; _me = me; _logger = logger;
    }

    public async Task<Result> Handle(RejectUserCommand req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Reason))
            return Result.Failure(Error.Custom("Approval.ReasonRequired", "Rejection reason is required."));

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == req.TargetUserId && !u.IsDeleted, ct);
        if (user is null) return Result.Failure(Error.UserNotFound);
        if (user.Status != UserStatus.Pending)
            return Result.Failure(Error.Custom("Approval.NotPending", $"Cannot reject a user in {user.Status} status."));

        // Authorization — see ApproveUserHandler for the matrix.
        if (_me.Role is UserRole.Admin or UserRole.Manager)
        {
            if (user.Role != UserRole.Vendor)
                return Result.Failure(Error.Custom("Approval.Forbidden",
                    "Crew registrations are handled by the referring vendor."));
        }
        else if (_me.Role == UserRole.Vendor)
        {
            if (user.Role != UserRole.Crew)
                return Result.Failure(Error.Custom("Approval.Forbidden",
                    "Vendors can only act on crew registrations."));
            var myRef = await _db.Users
                .Where(u => u.Id == _me.UserId)
                .Select(u => u.ReferralCode)
                .FirstOrDefaultAsync(ct);
            if (string.IsNullOrEmpty(myRef) || user.ReferralCodeUsed != myRef)
                return Result.Failure(Error.Custom("Approval.Forbidden",
                    "This crew did not register under your referral code."));
        }
        else
        {
            return Result.Failure(Error.Custom("Approval.Forbidden",
                "Your role cannot reject registrations."));
        }

        user.Reject(req.RejectedByUserId, req.Reason);
        await _uow.SaveChangesAsync(ct);

        var canRetryAt = (user.RejectedAt ?? DateTime.UtcNow) + CoolDown;

        await _audit.LogAsync(AuditAction.UserStatusChanged, nameof(User), user.Id.ToString(),
            newValues: new { Status = user.Status.ToString(), Reason = req.Reason },
            additionalData: $"Rejected by {req.RejectedByUserId}",
            cancellationToken: ct);

        // Side-effects (best-effort).
        if (!string.IsNullOrEmpty(user.Email))
        {
            try { await _email.SendRejectionEmailAsync(user.Email, user.FullName, req.Reason, canRetryAt, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "Rejection email failed for {UserId}.", user.Id); }
        }

        try
        {
            var sms = $"EventWOS: Your registration was not approved. Reason: {req.Reason}. You can re-apply after {canRetryAt:dd MMM, HH:mm} UTC.";
            await _sms.SendAsync(user.Mobile, sms, ct);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Rejection SMS failed for {UserId}.", user.Id); }

        try
        {
            await _push.PushToUserAsync(user.Id, "RegistrationRejected",
                new { userId = user.Id, reason = req.Reason, canRetryAt }, ct);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Rejection SignalR push failed for {UserId}.", user.Id); }

        return Result.Success();
    }
}
