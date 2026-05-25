using Asp.Versioning;
using EventWOS.Api.Authorization;
using EventWOS.Application.AuditLogs.Queries;
using EventWOS.Shared.Common;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EventWOS.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/audit-logs")]
[ApiVersion("1.0")]
[Authorize]
[Permission("audit:read")]
public sealed class AuditLogsController : ControllerBase
{
    private readonly IMediator _mediator;
    public AuditLogsController(IMediator mediator) => _mediator = mediator;

    /// <summary>
    /// Paginated audit log — filterable by entityType, entityId, userId, action, and date range.
    /// Admin and Managers with audit:read permission only.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetLogs(
        [FromQuery] string?   entityType = null,
        [FromQuery] string?   entityId   = null,
        [FromQuery] Guid?     userId     = null,
        [FromQuery] string?   action     = null,
        [FromQuery] DateTime? from       = null,
        [FromQuery] DateTime? to         = null,
        [FromQuery] int       page       = 1,
        [FromQuery] int       pageSize   = 50,
        CancellationToken ct             = default)
    {
        var result = await _mediator.Send(
            new GetAuditLogsQuery(entityType, entityId, userId, action, from, to, page, pageSize), ct);

        return result.IsSuccess
            ? Ok(ApiResponse<PagedResult<AuditLogDto>>.Ok(result.Value))
            : BadRequest(ApiResponse<PagedResult<AuditLogDto>>.Fail(result.Error.Message));
    }
}
