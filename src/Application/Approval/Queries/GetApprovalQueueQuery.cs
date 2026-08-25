using EventOpsOracle.Application.Approval.DTOs;
using EventOpsOracle.Shared.Result;
using MediatR;

namespace EventOpsOracle.Application.Approval.Queries;

/// <summary>
/// Returns Pending self-registrations grouped by role. Admin/Manager UI
/// renders two tabs (Vendors / Crew) sourced from the same response.
/// </summary>
public sealed record GetApprovalQueueQuery() : IRequest<Result<ApprovalQueueDto>>;
