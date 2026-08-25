using Asp.Versioning;
using EventOpsOracle.Api.Authorization;
using EventOpsOracle.Application.Announcements.Commands;
using EventOpsOracle.Application.Announcements.DTOs;
using EventOpsOracle.Application.Announcements.Queries;
using EventOpsOracle.Domain.Enums;
using EventOpsOracle.Domain.Interfaces;
using EventOpsOracle.Shared.Common;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EventOpsOracle.Api.Controllers;

/// <summary>
/// Event notifications ("announcements"): Admin/Manager broadcasts a rich-text
/// message with optional attachments to an event's vendors and/or crew, and
/// recipients read them back on their dashboard.
///
/// Send is gated on events:write. The read paths deliberately are NOT
/// permission-gated — crew and vendors hold no events:* permission — so
/// authorization is done inside the handlers against the announcement's
/// audience and the caller's connection to the event.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}")]
[Authorize]
[Produces("application/json")]
public sealed class AnnouncementsController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ICurrentUser _currentUser;

    public AnnouncementsController(IMediator mediator, ICurrentUser currentUser)
    {
        _mediator = mediator;
        _currentUser = currentUser;
    }

    /// <summary>Broadcast a notification to an event's vendors and/or crew. Requires events:write.</summary>
    [Permission("events:write")]
    [HttpPost("events/{eventId:guid}/announcements")]
    [ProducesResponseType(typeof(ApiResponse<SendAnnouncementResultDto>), 200)]
    [ProducesResponseType(typeof(ApiResponse), 400)]
    public async Task<IActionResult> Send(Guid eventId, [FromBody] SendAnnouncementRequest body, CancellationToken ct)
    {
        var cmd = new SendEventAnnouncementCommand(
            EventId: eventId,
            Audience: body.Audience,
            Subject: body.Subject ?? string.Empty,
            BodyHtml: body.BodyHtml ?? string.Empty,
            AttachmentFileIds: body.AttachmentFileIds ?? new List<Guid>(),
            SentByUserId: _currentUser.UserId!.Value);

        var result = await _mediator.Send(cmd, ct);
        if (result.IsFailure)
        {
            var status = result.Error.Code == "General.NotFound" ? 404 : 400;
            return StatusCode(status, ApiResponse.Fail(result.Error.Message));
        }
        return Ok(ApiResponse<SendAnnouncementResultDto>.Ok(result.Value));
    }

    /// <summary>Notification history for one event — visible to Admin/Manager and to the event's own vendors/crew.</summary>
    [HttpGet("events/{eventId:guid}/announcements")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<EventAnnouncementDto>>), 200)]
    [ProducesResponseType(typeof(ApiResponse), 403)]
    public async Task<IActionResult> ListForEvent(Guid eventId, CancellationToken ct)
    {
        var query = new GetEventAnnouncementsQuery(
            EventId: eventId,
            RequestingUserId: _currentUser.UserId!.Value,
            RequestingUserRole: _currentUser.Role ?? UserRole.Crew,
            IsPrivileged: _currentUser.HasPermission("events:read"));

        var result = await _mediator.Send(query, ct);
        if (result.IsFailure)
            return StatusCode(MapStatus(result.Error.Code), ApiResponse.Fail(result.Error.Message));

        return Ok(ApiResponse<IReadOnlyList<EventAnnouncementDto>>.Ok(result.Value));
    }

    /// <summary>The caller's own notification inbox across every event they're on.</summary>
    [HttpGet("announcements/mine")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<EventAnnouncementDto>>), 200)]
    public async Task<IActionResult> Mine([FromQuery] int take = 50, CancellationToken ct = default)
    {
        var query = new GetMyAnnouncementsQuery(
            UserId: _currentUser.UserId!.Value,
            Role: _currentUser.Role ?? UserRole.Crew,
            Take: take);

        var result = await _mediator.Send(query, ct);
        if (result.IsFailure)
            return StatusCode(MapStatus(result.Error.Code), ApiResponse.Fail(result.Error.Message));

        return Ok(ApiResponse<IReadOnlyList<EventAnnouncementDto>>.Ok(result.Value));
    }

    /// <summary>Marks one announcement read for the caller (idempotent).</summary>
    [HttpPost("announcements/{id:guid}/read")]
    [ProducesResponseType(typeof(ApiResponse), 200)]
    public async Task<IActionResult> MarkRead(Guid id, CancellationToken ct)
    {
        var result = await _mediator.Send(new MarkAnnouncementReadCommand(id, _currentUser.UserId!.Value), ct);
        if (result.IsFailure)
            return StatusCode(MapStatus(result.Error.Code), ApiResponse.Fail(result.Error.Message));
        return Ok(ApiResponse.Ok("Marked as read."));
    }

    /// <summary>
    /// Streams an attachment. This is the URL behind every attachment LINK in
    /// the UI and in the WhatsApp deep link — images/PDFs render inline in the
    /// browser tab, anything else downloads.
    /// </summary>
    [HttpGet("announcements/{id:guid}/attachments/{fileId:guid}")]
    [ProducesResponseType(200)]
    [ProducesResponseType(typeof(ApiResponse), 403)]
    [ProducesResponseType(typeof(ApiResponse), 404)]
    public async Task<IActionResult> DownloadAttachment(Guid id, Guid fileId, CancellationToken ct)
    {
        var query = new DownloadAnnouncementAttachmentQuery(
            AnnouncementId: id,
            FileId: fileId,
            RequestingUserId: _currentUser.UserId!.Value,
            RequestingUserRole: _currentUser.Role ?? UserRole.Crew,
            IsPrivileged: _currentUser.HasPermission("events:read"));

        var result = await _mediator.Send(query, ct);
        if (result.IsFailure)
            return StatusCode(MapStatus(result.Error.Code), ApiResponse.Fail(result.Error.Message));

        var file = result.Value;
        var inline = file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                  || file.ContentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase);

        if (inline)
        {
            // Content-Disposition: inline so a click opens a viewer tab
            // instead of a download — "click and view", per spec.
            Response.Headers.ContentDisposition =
                $"inline; filename=\"{System.Net.WebUtility.UrlEncode(file.OriginalFileName)}\"";
            return File(file.Content, file.ContentType);
        }

        return File(file.Content, file.ContentType, file.OriginalFileName);
    }

    private static int MapStatus(string code) => code switch
    {
        "Auth.Unauthorized" => 403,
        "General.NotFound"  => 404,
        _ => 400
    };
}

/// <summary>JSON body for the send endpoint.</summary>
public sealed class SendAnnouncementRequest
{
    public AnnouncementAudience Audience { get; set; } = AnnouncementAudience.Both;
    public string? Subject { get; set; }
    public string? BodyHtml { get; set; }
    public List<Guid>? AttachmentFileIds { get; set; }
}
