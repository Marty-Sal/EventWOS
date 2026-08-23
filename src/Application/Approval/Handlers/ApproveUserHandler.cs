using EventWOS.Application.Approval.Commands;
using EventWOS.Application.Auth.Interfaces;
using EventWOS.Application.Common;
using EventWOS.Application.Interfaces;
using EventWOS.Application.Notifications.Abstractions;
using EventWOS.Application.Notifications.Contracts;
using EventWOS.Domain.Entities;
using EventWOS.Domain.Enums;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
    private readonly INotificationDispatcher _notifications;
    private readonly ICurrentUser _me;
    private readonly AppUrlOptions _appUrls;
    private readonly ILogger<ApproveUserHandler> _logger;

    public ApproveUserHandler(
        IAppDbContext db, IUnitOfWork uow, IAuditLogger audit,
        IEmailService email, IOtpService smsService, ISmsProvider sms,
        INotificationPusher push,
        INotificationDispatcher notifications,
        ICurrentUser me,
        IOptions<AppUrlOptions> appUrls,
        ILogger<ApproveUserHandler> logger)
    {
        _db = db; _uow = uow; _audit = audit;
        _email = email; _smsService = smsService; _sms = sms;
        _push = push; _notifications = notifications;
        _me = me; _appUrls = appUrls.Value; _logger = logger;
    }

    public async Task<Result<ApproveUserResponse>> Handle(ApproveUserCommand req, CancellationToken ct)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == req.TargetUserId && !u.IsDeleted, ct);
        if (user is null) return Result.Failure<ApproveUserResponse>(Error.UserNotFound);
        if (user.Status != UserStatus.Pending)
            return Result.Failure<ApproveUserResponse>(Error.Custom(
                "Approval.NotPending", $"Cannot approve a user in {user.Status} status."));

        // ── Authorization (defence in depth; controller does a coarse pass) ──
        //   Admin / Manager → can approve Vendor accounts only.
        //   Vendor          → can approve Crew accounts whose ReferralCodeUsed
        //                     matches THIS vendor's referral code.
        //   Anyone else     → forbidden.
        if (_me.Role is UserRole.Admin or UserRole.Manager)
        {
            if (user.Role != UserRole.Vendor)
                return Result.Failure<ApproveUserResponse>(Error.Custom(
                    "Approval.Forbidden",
                    "Crew registrations are approved by the referring vendor, not by managers."));
        }
        else if (_me.Role == UserRole.Vendor)
        {
            if (user.Role != UserRole.Crew)
                return Result.Failure<ApproveUserResponse>(Error.Custom(
                    "Approval.Forbidden", "Vendors can only approve crew registrations."));
            var myRef = await _db.Users
                .Where(u => u.Id == _me.UserId)
                .Select(u => u.ReferralCode)
                .FirstOrDefaultAsync(ct);
            if (string.IsNullOrEmpty(myRef) || user.ReferralCodeUsed != myRef)
                return Result.Failure<ApproveUserResponse>(Error.Custom(
                    "Approval.Forbidden", "This crew did not register under your referral code."));
        }
        else
        {
            return Result.Failure<ApproveUserResponse>(Error.Custom(
                "Approval.Forbidden", "Your role cannot approve registrations."));
        }

        var oldStatus = user.Status;
        user.Approve(req.ApprovedByUserId);

        // Staged before the save so the approval and its inbox record commit
        // together. Note the three side-effects further down are all wrapped in
        // catch/LogWarning: if SendGrid and the SMS provider are both having a bad
        // day, the account is approved and NOBODY ever told the user. This row is
        // the durable record that survives that -- it is waiting in their inbox the
        // first time they sign in.
        //
        // InApp ONLY, deliberately. The email below is a real onboarding email:
        // branded, with the vendor's referral code and a share link for recruiting
        // their crew. The platform's ACCOUNT_APPROVED email is a one-line courtesy
        // note. Letting both go out means the welcome email arrives next to a worse
        // copy of itself. When the platform can carry that richer body per template,
        // drop SendApprovalEmailAsync and this channel restriction together.
        _notifications.Enqueue(new NotificationRequest(
            NotificationTemplateCodes.AccountApproved,
            RecipientUserId: user.Id,
            // One approval per user, so the transition alone is a stable key: a
            // double-clicked Approve cannot welcome the same person twice.
            BusinessEventKey: $"user:{user.Id}:approved",
            Data: new Dictionary<string, string?>
            {
                [NotificationTokens.RecipientName] = user.FullName,
                [NotificationTokens.Role]          = user.Role.ToString()
            },
            ActorUserId: req.ApprovedByUserId,
            Channels: new[] { NotificationChannel.InApp }));

        await _uow.SaveChangesAsync(ct);

        await _audit.LogAsync(AuditAction.UserStatusChanged, nameof(User), user.Id.ToString(),
            oldValues: new { Status = oldStatus.ToString() },
            newValues: new { Status = user.Status.ToString() },
            additionalData: $"Approved by {req.ApprovedByUserId}",
            cancellationToken: ct);

        // ── Side-effects (best-effort; failures must not break the approval) ─
        // SendGrid email + SMS both link to this. The /login/<portal> shape
        // matches the three-portal split landing in Phase 5.
        //
        // Resolution order (first non-empty wins):
        //   1. AppUrls:BaseUrl section in IConfiguration (preferred — typed)
        //   2. APP_BASE_URL env var (legacy — kept for deploy backward-compat)
        //   3. Hard default "https://eventwos.app" (matches the typed option's default)
        var baseUrl = !string.IsNullOrWhiteSpace(_appUrls.BaseUrl)
            ? _appUrls.BaseUrl
            : (Environment.GetEnvironmentVariable("APP_BASE_URL") ?? "https://eventwos.app");
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
