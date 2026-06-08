using EventWOS.Api.Authorization;
using Asp.Versioning;
using EventWOS.Application.ScopeOfWork.Commands;
using EventWOS.Application.ScopeOfWork.DTOs;
using EventWOS.Application.ScopeOfWork.Queries;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Common;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EventWOS.Api.Controllers;

/// <summary>
/// Admin-managed global catalog of scope-of-work categories used to staff
/// events (Box Office, Gates, F&amp;B, etc.).
///
/// AUTH PATTERN (memory rule #29): every endpoint uses
/// <c>[Permission("scope_of_work:*")]</c> instead of bare role attributes.
/// Read endpoints are open to anyone with <c>scope_of_work:read</c> — that
/// includes Manager (so they can pick categories when creating events) and
/// Admin (who can also edit). Write endpoints require <c>scope_of_work:write</c>
/// which by seeder default is Admin-only.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/scope-of-work")]
[Authorize]
[Produces("application/json")]
public sealed class ScopeOfWorkController : ControllerBase
{
    private readonly IMediator    _mediator;
    private readonly ICurrentUser _currentUser;

    public ScopeOfWorkController(IMediator mediator, ICurrentUser currentUser)
    {
        _mediator    = mediator;
        _currentUser = currentUser;
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    [Permission("scope_of_work:read")]
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? search          = null,
        [FromQuery] bool    includeArchived = false,
        [FromQuery] int     page            = 1,
        [FromQuery] int     pageSize        = 50,
        CancellationToken   ct              = default)
    {
        var res = await _mediator.Send(
            new GetScopesOfWorkQuery(search, includeArchived, page, pageSize), ct);
        return res.IsSuccess
            ? Ok(ApiResponse<PagedResult<ScopeOfWorkDto>>.Ok(res.Value))
            : BadRequest(ApiResponse<PagedResult<ScopeOfWorkDto>>.Fail(res.Error.Message));
    }

    // ── Write ─────────────────────────────────────────────────────────────────

    public sealed record CreateScopeRequest(string Name, string? Description);
    public sealed record UpdateScopeRequest(string Name, string? Description);

    [Permission("scope_of_work:write")]
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateScopeRequest req, CancellationToken ct)
    {
        var res = await _mediator.Send(
            new CreateScopeOfWorkCommand(req.Name, req.Description, _currentUser.UserId!.Value), ct);
        return res.IsSuccess
            ? Ok(ApiResponse<ScopeOfWorkDto>.Ok(res.Value))
            : BadRequest(ApiResponse<ScopeOfWorkDto>.Fail(res.Error.Message));
    }

    [Permission("scope_of_work:write")]
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateScopeRequest req, CancellationToken ct)
    {
        var res = await _mediator.Send(
            new UpdateScopeOfWorkCommand(id, req.Name, req.Description, _currentUser.UserId!.Value), ct);
        return res.IsSuccess
            ? Ok(ApiResponse<ScopeOfWorkDto>.Ok(res.Value))
            : BadRequest(ApiResponse<ScopeOfWorkDto>.Fail(res.Error.Message));
    }

    [Permission("scope_of_work:write")]
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Archive(Guid id, CancellationToken ct)
    {
        var res = await _mediator.Send(
            new ArchiveScopeOfWorkCommand(id, _currentUser.UserId!.Value), ct);
        return res.IsSuccess
            ? Ok(ApiResponse.Ok("Archived."))
            : BadRequest(ApiResponse.Fail(res.Error.Message));
    }

    [Permission("scope_of_work:write")]
    [HttpPost("{id:guid}/restore")]
    public async Task<IActionResult> Restore(Guid id, CancellationToken ct)
    {
        var res = await _mediator.Send(
            new RestoreScopeOfWorkCommand(id, _currentUser.UserId!.Value), ct);
        return res.IsSuccess
            ? Ok(ApiResponse.Ok("Restored."))
            : BadRequest(ApiResponse.Fail(res.Error.Message));
    }
}
