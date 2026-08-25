using EventOpsOracle.Api.Authorization;
using Asp.Versioning;
using EventOpsOracle.Application.Venues.Commands;
using EventOpsOracle.Application.Venues.DTOs;
using EventOpsOracle.Application.Venues.Queries;
using EventOpsOracle.Domain.Interfaces;
using EventOpsOracle.Shared.Common;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EventOpsOracle.Api.Controllers;

/// <summary>
/// Admin-managed venue catalog, used from Settings → Venue. Same auth
/// pattern as ScopeOfWorkController (memory rule #29): permission-gated,
/// read open to Manager+Admin, write Admin-only by seeder default.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/venues")]
[Authorize]
[Produces("application/json")]
public sealed class VenuesController : ControllerBase
{
    private readonly IMediator    _mediator;
    private readonly ICurrentUser _currentUser;

    public VenuesController(IMediator mediator, ICurrentUser currentUser)
    {
        _mediator    = mediator;
        _currentUser = currentUser;
    }

    [Permission("venues:read")]
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? search          = null,
        [FromQuery] bool    includeArchived = false,
        [FromQuery] int     page            = 1,
        [FromQuery] int     pageSize        = 50,
        [FromQuery] string? state           = null,
        CancellationToken   ct              = default)
    {
        var res = await _mediator.Send(new GetVenuesQuery(search, includeArchived, page, pageSize, state), ct);
        return res.IsSuccess
            ? Ok(ApiResponse<PagedResult<VenueDto>>.Ok(res.Value))
            : BadRequest(ApiResponse<PagedResult<VenueDto>>.Fail(res.Error.Message));
    }

    /// <summary>
    /// Distinct states among active venues — feeds the "pick a state
    /// first" step of the Event-creation venue picker.
    /// </summary>
    [Permission("venues:read")]
    [HttpGet("states")]
    public async Task<IActionResult> States(CancellationToken ct)
    {
        var res = await _mediator.Send(new GetVenueStatesQuery(), ct);
        return res.IsSuccess
            ? Ok(ApiResponse<IReadOnlyList<string>>.Ok(res.Value))
            : BadRequest(ApiResponse<IReadOnlyList<string>>.Fail(res.Error.Message));
    }

    [Permission("venues:read")]
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var res = await _mediator.Send(new GetVenueByIdQuery(id), ct);
        return res.IsSuccess
            ? Ok(ApiResponse<VenueDto>.Ok(res.Value))
            : NotFound(ApiResponse<VenueDto>.Fail(res.Error.Message));
    }

    public sealed record VenueRequest(
        string Name, string AddressLine1, string? AddressLine2, string? ShortAddress, string City,
        string? State, string? PostalCode, string? Country,
        double? Latitude, double? Longitude, string? Notes);

    [Permission("venues:write")]
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] VenueRequest req, CancellationToken ct)
    {
        var res = await _mediator.Send(new CreateVenueCommand(
            req.Name, req.AddressLine1, req.AddressLine2, req.ShortAddress, req.City,
            req.State, req.PostalCode, req.Country, req.Latitude, req.Longitude,
            req.Notes, _currentUser.UserId!.Value), ct);
        return res.IsSuccess
            ? Ok(ApiResponse<VenueDto>.Ok(res.Value))
            : BadRequest(ApiResponse<VenueDto>.Fail(res.Error.Message));
    }

    [Permission("venues:write")]
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] VenueRequest req, CancellationToken ct)
    {
        var res = await _mediator.Send(new UpdateVenueCommand(
            id, req.Name, req.AddressLine1, req.AddressLine2, req.ShortAddress, req.City,
            req.State, req.PostalCode, req.Country, req.Latitude, req.Longitude,
            req.Notes, _currentUser.UserId!.Value), ct);
        return res.IsSuccess
            ? Ok(ApiResponse<VenueDto>.Ok(res.Value))
            : BadRequest(ApiResponse<VenueDto>.Fail(res.Error.Message));
    }

    [Permission("venues:write")]
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Archive(Guid id, CancellationToken ct)
    {
        var res = await _mediator.Send(new ArchiveVenueCommand(id, _currentUser.UserId!.Value), ct);
        return res.IsSuccess ? Ok(ApiResponse.Ok("Archived.")) : BadRequest(ApiResponse.Fail(res.Error.Message));
    }

    [Permission("venues:write")]
    [HttpPost("{id:guid}/restore")]
    public async Task<IActionResult> Restore(Guid id, CancellationToken ct)
    {
        var res = await _mediator.Send(new RestoreVenueCommand(id, _currentUser.UserId!.Value), ct);
        return res.IsSuccess ? Ok(ApiResponse.Ok("Restored.")) : BadRequest(ApiResponse.Fail(res.Error.Message));
    }
}
