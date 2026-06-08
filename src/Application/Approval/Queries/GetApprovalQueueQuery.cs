using EventWOS.Application.Approval.DTOs;
using EventWOS.Shared.Result;
using MediatR;

namespace EventWOS.Application.Approval.Queries;

/// <summary>
/// Returns Pending self-registrations grouped by role. Admin/Manager UI
/// renders two tabs (Vendors / Crew) sourced from the same response.
/// </summary>
public sealed record GetApprovalQueueQuery() : IRequest<Result<ApprovalQueueDto>>;
