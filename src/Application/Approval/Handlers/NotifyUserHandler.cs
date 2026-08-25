using EventOpsOracle.Application.Approval.Commands;
using EventOpsOracle.Application.Common;
using EventOpsOracle.Application.Interfaces;
using EventOpsOracle.Domain.Entities;
using EventOpsOracle.Domain.Enums;
using EventOpsOracle.Domain.Interfaces;
using EventOpsOracle.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EventOpsOracle.Application.Approval.Handlers;

/// <summary>
/// Sends the applicant a "we need more information" email while they're
/// still Pending — a lighter-weight action than Approve/Reject that lets
/// the reviewer ask a question without deciding yet. Doesn't touch Status.
/// Same authorization matrix as ApproveUserHandler/RejectUserHandler.
/// </summary>
public sealed class NotifyUserHandler : IRequestHandler<NotifyUserCommand, Result>
{
    private readonly IAppDbContext _db;
    private readonly IAuditLogger _audit;
    private readonly IEmailService _email;
    private readonly ICurrentUser _me;
    private readonly ILogger<NotifyUserHandler> _logger;

    public NotifyUserHandler(
        IAppDbContext db, IAuditLogger audit, IEmailService email,
        ICurrentUser me, ILogger<NotifyUserHandler> logger)
    {
        _db = db; _audit = audit; _email = email; _me = me; _logger = logger;
    }

    public async Task<Result> Handle(NotifyUserCommand req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Message))
            return Result.Failure(Error.Custom("Approval.MessageRequired", "Please enter a message to send."));

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == req.TargetUserId && !u.IsDeleted, ct);
        if (user is null) return Result.Failure(Error.UserNotFound);
        if (user.Status != UserStatus.Pending)
            return Result.Failure(Error.Custom("Approval.NotPending", $"Cannot notify a user in {user.Status} status."));

        // Authorization — identical matrix to Approve/Reject.
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
                "Your role cannot act on registrations."));
        }

        if (string.IsNullOrWhiteSpace(user.Email))
            return Result.Failure(Error.Custom("Approval.NoEmail",
                "This applicant did not provide an email address, so they can't be notified this way."));

        var subject = "OpsOracle — we need a bit more information";
        var safeMessage = System.Net.WebUtility.HtmlEncode(req.Message).Replace("\n", "<br/>");
        var html = $@"
            <p>Hi {System.Net.WebUtility.HtmlEncode(user.FullName)},</p>
            <p>Thanks for registering with EventOpsOracle. Before we can finish reviewing your application, we need a bit more information:</p>
            <blockquote style=""margin:12px 0;padding:8px 12px;border-left:3px solid #6366f1;color:#374151;"">{safeMessage}</blockquote>
            <p>Please reply to this email or update your details, and we'll continue the review as soon as we hear back.</p>
            <p>— The OpsOracle Team</p>";
        var plainText = $"Hi {user.FullName},\n\nThanks for registering with EventOpsOracle. Before we can finish reviewing your application, we need a bit more information:\n\n{req.Message}\n\nPlease reply to this email or update your details, and we'll continue the review as soon as we hear back.\n\n— The OpsOracle Team";

        var sent = await _email.SendAsync(user.Email, subject, html, plainText, ct);
        if (!sent)
        {
            _logger.LogWarning("Notify email failed to send for {UserId}.", user.Id);
            return Result.Failure(Error.Custom("Approval.EmailFailed", "Could not send the email — please try again."));
        }

        await _audit.LogAsync(AuditAction.UserNotified, nameof(User), user.Id.ToString(),
            additionalData: $"Notified by {req.RequestedByUserId}: {req.Message}",
            cancellationToken: ct);

        return Result.Success();
    }
}
