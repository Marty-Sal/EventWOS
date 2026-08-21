using Asp.Versioning;
using EventWOS.Application.Lookups.Queries;
using EventWOS.Shared.Common;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EventWOS.Api.Controllers;

/// <summary>
/// Small, static reference-data lookups shared across the app (currently
/// just the India states + union territories list). Deliberately
/// anonymous — Vendor/Crew self-registration pages need the states
/// dropdown before the user has an account, same reasoning as
/// TermsController.GetCurrent.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/lookups")]
[Produces("application/json")]
[AllowAnonymous]
public sealed class LookupsController : ControllerBase
{
    private readonly IMediator _mediator;
    public LookupsController(IMediator mediator) => _mediator = mediator;

    /// <summary>Canonical list of the 28 Indian states + 8 union territories, in display order.</summary>
    [HttpGet("indian-states")]
    public async Task<IActionResult> GetIndianStates(CancellationToken ct)
    {
        var res = await _mediator.Send(new GetIndianStatesQuery(), ct);
        return Ok(ApiResponse<object>.Ok(res.Value));
    }
}
