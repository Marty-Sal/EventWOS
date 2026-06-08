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
/// Approves a Pending self-registration. Three side-effects all fired
/// post-save so a rolled-back transaction never sends notifications:
///   1. SendGrid welcome email (with referral code for Vendors).
///   2. SMS welcome message via the project's standard ISmsProvider.
///   3. SignalR push on the user's group so any "waiting for approval"
///      page they have open immediately bounces to the login screen.
///
/// All three side-effects are best-effort — a SendGrid outage or a
/// missing phone provider must NOT roll back the approval itself.
/// </summary>
public sealed class ApproveUserHandler : IRequestHandler<ApproveUserCommand, Result<ApproveUserResponse>>
{
    private readonly IAppDbContext _db;
    private readonly IUnitOfWork _uow;
    private readonly IAuditLogger _audit;
    private readonly IEmailService _email;
    private readonly IOtpService _smsService;       // reuse — it owns the ISmsProvider
    private readonly ISmsProvider _sms;
    private readonly INotificationPusher _push;
    private readonly ILogger<ApproveUserHandler> _logger;

    public ApproveUserHandler(
        IAppDbContext db, IUnitOfWork uow, IAuditLogger audit,
        IEmailService email, IOtpService smsService, ISmsProvider sms,
        INotificationPusher push,
        ILogger<ApproveUserHandler> logger)
    {
        _db = db; _uow = uow; _audit = audit;
        _email = email; _smsService = smsService; _sms = sms;
        _push = push; _logger = logger;
    }

    public async Task<Result<ApproveUserResponse>> Handle(ApproveUserCommand req, CancellationToken ct)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == req.TargetUserId && !u.IsDeleted, ct);
        if (user is null) return Result.Failure<ApproveUserResponse>(Error.UserNotFound);
        if (user.Status != UserStatus.Pending)
            return Result.Failure<ApproveUserResponse>(Error.Custom(
                "Approval.NotPending", $"Cannot approve a user in {user.Status} status."));

        var oldStatus = user.Status;
        user.Approve(req.ApprovedByUserId);
        await _uow.SaveChangesAsync(ct);

        await _audit.LogAsync(AuditAction.UserStatusChanged, nameof(User), user.Id.ToString(),
            oldValues: new { Status = oldStatus.ToString() },
            newValues: new { Status = user.Status.ToString() },
            additionalData: $"Approved by {req.ApprovedByUserId}",
            cancellationToken: ct);

        // ── Side-effects (best-effort; failures must not break the approval) ─
        // Login URL is a hard default for now — wire to config in Phase 6.
        // SendGrid email + SMS both link to this. The /login/<portal> shape
        // matches the three-portal split landing in Phase 5.
        var baseUrl = Environment.GetEnvironmentVariable("APP_BASE_URL") ?? "https://eventwos.app";
        var loginUrl = baseUrl.TrimEnd('/')
                     + (user.Role == UserRole.Vendor ? "/login/vendor"
                        : user.Role == UserRole.Crew  ? "/login/crew"
                        : "/login/admin");

        if (!string.IsNullOrEmpty(user.Email))
        {
            try
            {
                await _email.SendApprovalEmailAsync(user.Email, user.FullName,
                    user.Role.ToString(), user.ReferralCode, loginUrl, ct);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Approval email failed for {UserId}.", user.Id); }
        }

        try
        {
            var sms = $"Welcome to EventWOS, {user.FullName}! Your {user.Role} account is approved. Sign in: {loginUrl}";
            await _sms.SendAsync(user.Mobile, sms, ct);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Approval SMS failed for {UserId}.", user.Id); }

        try
        {
            await _push.PushToUserAsync(user.Id, "RegistrationApproved",
                new { userId = user.Id, role = user.Role.ToString(), referralCode = user.ReferralCode }, ct);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Approval SignalR push failed for {UserId}.", user.Id); }

        return Result.Success(new ApproveUserResponse(user.Id, user.Role.ToString(), user.ReferralCode));
    }
}
