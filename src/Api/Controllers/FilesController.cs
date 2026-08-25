using Asp.Versioning;
using EventOpsOracle.Api.Authorization;
using EventOpsOracle.Application.Files.Commands;
using EventOpsOracle.Application.Files.DTOs;
using EventOpsOracle.Application.Files.Queries;
using EventOpsOracle.Domain.Enums;
using EventOpsOracle.Domain.Interfaces;
using EventOpsOracle.Shared.Common;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EventOpsOracle.Api.Controllers;

/// <summary>
/// Generic File & Image Storage endpoints. Upload flow: validate → generate
/// storage key → write bytes via IFileStorage → persist metadata. Download
/// flow: authenticate/authorize → load metadata → stream bytes back.
///
/// Every endpoint requires authentication — files are never public by
/// default. The one exception (anonymous Crew self-registration uploading
/// their own photo/ID before an account exists) is handled inline inside
/// RegisterCrewHandler, not through this controller.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/files")]
[Authorize]
[Produces("application/json")]
public sealed class FilesController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ICurrentUser _currentUser;
    private const long MaxUploadBytes = 20 * 1024 * 1024; // hard ceiling; per-DocumentType limits are enforced again in the handler

    public FilesController(IMediator mediator, ICurrentUser currentUser)
    {
        _mediator = mediator;
        _currentUser = currentUser;
    }

    /// <summary>
    /// Uploads a file for the current user (or, with files:manage, for another owner).
    /// multipart/form-data: File (binary), DocumentType (int), OwnerId (optional guid), EntityId (optional guid).
    /// </summary>
    [Permission("files:upload")]
    [HttpPost("upload")]
    [RequestSizeLimit(MaxUploadBytes)]
    [ProducesResponseType(typeof(ApiResponse<FileDocumentDto>), 200)]
    [ProducesResponseType(typeof(ApiResponse), 400)]
    [ProducesResponseType(typeof(ApiResponse), 403)]
    public async Task<IActionResult> Upload([FromForm] UploadFileRequest form, CancellationToken ct)
    {
        if (form.File is null || form.File.Length == 0)
            return BadRequest(ApiResponse.Fail("No file was provided."));
        if (form.File.Length > MaxUploadBytes)
            return BadRequest(ApiResponse.Fail("File is too large."));

        var ownerId = form.OwnerId ?? _currentUser.UserId!.Value;

        byte[] bytes;
        using (var ms = new MemoryStream())
        {
            await form.File.CopyToAsync(ms, ct);
            bytes = ms.ToArray();
        }

        var cmd = new UploadFileCommand(
            RequestingUserId: _currentUser.UserId!.Value,
            RequesterCanManageOthers: _currentUser.HasPermission("files:manage"),
            OwnerId: ownerId,
            EntityId: form.EntityId,
            DocumentType: form.DocumentType,
            Content: bytes,
            OriginalFileName: form.File.FileName,
            ContentType: form.File.ContentType);

        var result = await _mediator.Send(cmd, ct);
        if (result.IsFailure)
        {
            var status = result.Error.Code == "Auth.Unauthorized" ? 403 : 400;
            return StatusCode(status, ApiResponse.Fail(result.Error.Message));
        }
        return Ok(ApiResponse<FileDocumentDto>.Ok(result.Value));
    }

    /// <summary>Streams the file's bytes back. Identification documents additionally require files:read_identity and every access is audit-logged.</summary>
    [Permission("files:read")]
    [HttpGet("{id:guid}/download")]
    [ProducesResponseType(200)]
    [ProducesResponseType(typeof(ApiResponse), 403)]
    [ProducesResponseType(typeof(ApiResponse), 404)]
    public async Task<IActionResult> Download(Guid id, CancellationToken ct)
    {
        var query = new DownloadFileQuery(
            FileId: id,
            RequestingUserId: _currentUser.UserId!.Value,
            RequesterCanManageOthers: _currentUser.HasPermission("files:manage"),
            RequesterCanReadIdentity: _currentUser.HasPermission("files:read_identity"));

        var result = await _mediator.Send(query, ct);
        if (result.IsFailure)
        {
            var status = result.Error.Code switch
            {
                "Auth.Unauthorized" => 403,
                "General.NotFound"  => 404,
                _ => 400
            };
            return StatusCode(status, ApiResponse.Fail(result.Error.Message));
        }

        var file = result.Value;
        return File(file.Content, file.ContentType, file.OriginalFileName);
    }

    /// <summary>Soft-deletes the metadata row and best-effort deletes the underlying object(s) from storage.</summary>
    [Permission("files:upload")]
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(typeof(ApiResponse), 200)]
    [ProducesResponseType(typeof(ApiResponse), 403)]
    [ProducesResponseType(typeof(ApiResponse), 404)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var cmd = new DeleteFileCommand(
            FileId: id,
            RequestingUserId: _currentUser.UserId!.Value,
            RequesterCanManageOthers: _currentUser.HasPermission("files:manage"));

        var result = await _mediator.Send(cmd, ct);
        if (result.IsFailure)
        {
            var status = result.Error.Code switch
            {
                "Auth.Unauthorized" => 403,
                "General.NotFound"  => 404,
                _ => 400
            };
            return StatusCode(status, ApiResponse.Fail(result.Error.Message));
        }
        return Ok(ApiResponse.Ok("File deleted."));
    }
}

/// <summary>Model-bound multipart/form-data shape for the upload endpoint. A plain class (not a record) — ASP.NET Core's form binder needs settable properties, especially alongside an IFormFile.</summary>
public sealed class UploadFileRequest
{
    public IFormFile? File { get; set; }
    public DocumentType DocumentType { get; set; }
    public Guid? OwnerId { get; set; }
    public Guid? EntityId { get; set; }
}
