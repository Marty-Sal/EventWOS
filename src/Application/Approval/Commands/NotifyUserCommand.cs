using EventWOS.Shared.Result;
using MediatR;

namespace EventWOS.Application.Approval.Commands;

/// <summary>
/// Admin/Manager (for a pending Vendor) or Vendor (for a pending Crew
/// referred to them) asks the applicant for more information before
/// deciding — sent as an email, does NOT change the user's Status. Same
/// authorization matrix as Approve/Reject.
/// </summary>
public sealed record NotifyUserCommand(
    Guid    TargetUserId,
    Guid    RequestedByUserId,
    string  Message
) : IRequest<Result>;
