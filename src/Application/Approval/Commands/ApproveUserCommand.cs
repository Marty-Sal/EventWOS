using EventWOS.Shared.Result;
using MediatR;

namespace EventWOS.Application.Approval.Commands;

/// <summary>
/// Admin/Manager approves a Pending self-registration. Fires:
///   - Domain: user.Approve(approverId) — flips to Active, generates
///     ReferralCode for Vendors who don't already have one.
///   - SendGrid welcome email (best-effort).
///   - SMS welcome message via existing ISmsProvider.
///   - SignalR push to the user (in case they're sitting on a "pending"
///     page polling — they'll get an immediate prompt to sign in).
/// </summary>
public sealed record ApproveUserCommand(
    Guid TargetUserId,
    Guid ApprovedByUserId
) : IRequest<Result<ApproveUserResponse>>;

public sealed record ApproveUserResponse(
    Guid    UserId,
    string  Role,
    string? ReferralCode    // populated for Vendors so the UI can surface it
);
