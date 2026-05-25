using EventWOS.Api.Authorization;
using Asp.Versioning;
using EventWOS.Application.Analytics.Queries;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Common;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EventWOS.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/analytics")]
[ApiVersion("1.0")]
[Authorize]
public sealed class AnalyticsController : ControllerBase
{
    private readonly IMediator    _mediator;
    private readonly ICurrentUser _currentUser;

    public AnalyticsController(IMediator mediator, ICurrentUser currentUser)
    {
        _mediator     = mediator;
        _currentUser  = currentUser;
    }

    /// <summary>Get aggregated dashboard statistics (Admin/Manager).</summary>
    [Permission("reports:read")]
    [HttpGet("dashboard")]
    public async Task<IActionResult> GetDashboard(CancellationToken ct = default)
    {
        var result = await _mediator.Send(new GetDashboardStatsQuery(), ct);
        return Ok(ApiResponse<DashboardStatsDto>.Ok(result.Value));
    }
}
