using Asp.Versioning;
using EventOpsOracle.Api.Authorization;
using EventOpsOracle.Application.Locations;
using EventOpsOracle.Application.Locations.DTOs;
using EventOpsOracle.Shared.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EventOpsOracle.Api.Controllers;

/// <summary>
/// Server-side proxy to the configured location provider. Blazor talks to this
/// controller and never to the provider directly, which is what keeps provider
/// credentials (Google/Mappls keys, once we move off Nominatim) out of the
/// browser and lets us cache and rate-limit centrally.
///
/// Gated on the same "venues:write" permission as venue editing rather than
/// plain [Authorize]: place search is an admin authoring tool, and leaving it
/// open to every authenticated crew member would turn our server into a free
/// open geocoding proxy for anyone with a login.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/locations")]
[Authorize]
[Produces("application/json")]
public sealed class LocationsController : ControllerBase
{
    private readonly ILocationService _locations;
    private readonly ILogger<LocationsController> _logger;

    public LocationsController(ILocationService locations, ILogger<LocationsController> logger)
    {
        _locations = locations;
        _logger    = logger;
    }

    /// <summary>
    /// Autocomplete for the venue search box. Returns an empty list (200, not
    /// 400) for a short or blank query so the debounced UI can call it freely
    /// without treating "still typing" as an error state.
    /// </summary>
    [Permission("venues:write")]
    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string? q, CancellationToken ct)
    {
        try
        {
            var results = await _locations.SearchAsync(q ?? string.Empty, ct);
            return Ok(ApiResponse<IReadOnlyList<LocationSearchResult>>.Ok(results));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The client abandoned the request (next keystroke superseded it).
            // 499 is nginx's "client closed request" — nothing is logged as an
            // error because this is the debounce working as designed.
            return StatusCode(499);
        }
    }

    /// <summary>
    /// Structured address for an exact point — called after the admin drags the
    /// map marker.
    /// </summary>
    [Permission("venues:write")]
    [HttpGet("reverse")]
    public async Task<IActionResult> Reverse(
        [FromQuery] decimal latitude,
        [FromQuery] decimal longitude,
        CancellationToken ct)
    {
        // Validate server-side even though this endpoint is only advisory:
        // out-of-range values indicate a broken client, and echoing them into
        // the provider URL is pointless work.
        if (latitude is < -90m or > 90m)
            return BadRequest(ApiResponse<LocationDetails>.Fail("Latitude must be between -90 and 90."));
        if (longitude is < -180m or > 180m)
            return BadRequest(ApiResponse<LocationDetails>.Fail("Longitude must be between -180 and 180."));

        try
        {
            var details = await _locations.ReverseGeocodeAsync(latitude, longitude, ct);

            // No address at this point is a legitimate outcome (middle of a
            // field). The UI keeps the dragged coordinates and just shows no
            // address, so this is a 200 with a null payload, not a 404.
            return Ok(ApiResponse<LocationDetails?>.Ok(details));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return StatusCode(499);
        }
    }
}
