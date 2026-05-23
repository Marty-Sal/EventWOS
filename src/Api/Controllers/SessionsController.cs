using Asp.Versioning;
using EventWOS.Application.Sessions.Commands;
using EventWOS.Application.Sessions.Queries;
using EventWOS.Domain.Enums;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Common;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EventWOS.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/sessions")]
[Authorize]
[Produces("application/json")]
public sealed class SessionsController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ICurrentUser _currentUser;

    public SessionsController(IMediator mediator, ICurrentUser currentUser)
    {
        _mediator = mediator;
        _currentUser = currentUser;
    }

    /// <summary>Get all active sessions for current user.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<SessionDto>>), 200)]
    public async Task<IActionResult> GetSessions(CancellationToken ct)
    {
        var result = await _mediator.Send(new GetSessionsQuery(_currentUser.UserId!.Value), ct);
        return Ok(ApiResponse<IReadOnlyList<SessionDto>>.Ok(result.Value));
    }

    /// <summary>Revoke a specific session.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(typeof(ApiResponse), 200)]
    public async Task<IActionResult> RevokeSession(Guid id, CancellationToken ct)
    {
        var isAdmin = _currentUser.IsInRole(UserRole.Admin);
        var command = new RevokeSessionCommand(id, _currentUser.UserId!.Value, isAdmin);
        var result = await _mediator.Send(command, ct);
        return result.IsSuccess ? Ok(ApiResponse.Ok("Session revoked.")) : BadRequest(ApiResponse.Fail(result.Error.Message));
    }
}
