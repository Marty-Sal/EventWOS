using EventWOS.Shared.Result;
using MediatR;

namespace EventWOS.Application.Approval.Commands;

/// <summary>
/// Admin/Manager rejects a Pending self-registration. Reason is mandatory
/// and surfaces verbatim in the rejection email + audit log. Sets
/// RejectedAt — the 24h cool-down window starts now.
/// </summary>
public sealed record RejectUserCommand(
    Guid    TargetUserId,
    Guid    RejectedByUserId,
    string  Reason
) : IRequest<Result>;
