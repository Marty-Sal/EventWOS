using Asp.Versioning;
using EventWOS.Application.Auth.Commands;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Common;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EventWOS.Api.Controllers;

/// <summary>Authentication endpoints — OTP flow, token refresh, logout.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/auth")]
[Produces("application/json")]
public sealed class AuthController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ICurrentUser _currentUser;

    public AuthController(IMediator mediator, ICurrentUser currentUser)
    {
        _mediator = mediator;
        _currentUser = currentUser;
    }

    /// <summary>Step 1: Request OTP for mobile number.</summary>
    [HttpPost("request-otp")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<RequestOtpResponse>), 200)]
    [ProducesResponseType(typeof(ApiResponse), 400)]
    public async Task<IActionResult> RequestOtp(
        [FromBody] RequestOtpDto dto,
        CancellationToken ct)
    {
        var command = new RequestOtpCommand(
            dto.Mobile,
            dto.DeviceId,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            Request.Headers.UserAgent);

        var result = await _mediator.Send(command, ct);

        if (result.IsFailure)
            return BadRequest(ApiResponse<RequestOtpResponse>.Fail(result.Error.Message));

        return Ok(ApiResponse<RequestOtpResponse>.Ok(result.Value));
    }

    /// <summary>Step 2: Verify OTP and receive JWT tokens.</summary>
    [HttpPost("verify-otp")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<AuthResponse>), 200)]
    [ProducesResponseType(typeof(ApiResponse), 400)]
    [ProducesResponseType(typeof(ApiResponse), 401)]
    public async Task<IActionResult> VerifyOtp(
        [FromBody] VerifyOtpDto dto,
        CancellationToken ct)
    {
        var command = new VerifyOtpCommand(
            dto.Mobile,
            dto.Otp,
            dto.OtpRequestId,
            dto.DeviceId,
            dto.DeviceName,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            Request.Headers.UserAgent);

        var result = await _mediator.Send(command, ct);

        if (result.IsFailure)
        {
            var statusCode = result.Error.Code.StartsWith("Auth.Account") ? 403 : 401;
            return StatusCode(statusCode, ApiResponse<AuthResponse>.Fail(result.Error.Message));
        }

        return Ok(ApiResponse<AuthResponse>.Ok(result.Value));
    }

    /// <summary>Refresh access token using refresh token.</summary>
    [HttpPost("refresh")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<AuthResponse>), 200)]
    [ProducesResponseType(typeof(ApiResponse), 401)]
    public async Task<IActionResult> Refresh([FromBody] RefreshDto dto, CancellationToken ct)
    {
        var command = new RefreshTokenCommand(
            dto.RefreshToken,
            dto.DeviceId,
            HttpContext.Connection.RemoteIpAddress?.ToString());

        var result = await _mediator.Send(command, ct);

        if (result.IsFailure)
            return Unauthorized(ApiResponse<AuthResponse>.Fail(result.Error.Message));

        return Ok(ApiResponse<AuthResponse>.Ok(result.Value));
    }

    /// <summary>Logout — revokes session and refresh token.</summary>
    [HttpPost("logout")]
    [Authorize]
    [ProducesResponseType(typeof(ApiResponse), 200)]
    public async Task<IActionResult> Logout([FromBody] LogoutDto dto, CancellationToken ct)
    {
        var command = new LogoutCommand(
            _currentUser.UserId!.Value,
            _currentUser.SessionId!.Value,
            dto.RefreshToken);

        var result = await _mediator.Send(command, ct);
        return Ok(ApiResponse.Ok("Logged out successfully."));
    }
}

// ─── Request DTOs ──────────────────────────────────────────────────────────
public sealed record RequestOtpDto(string Mobile, string? DeviceId);
public sealed record VerifyOtpDto(string Mobile, string Otp, Guid OtpRequestId, string? DeviceId, string? DeviceName);
public sealed record RefreshDto(string RefreshToken, string? DeviceId);
public sealed record LogoutDto(string RefreshToken);
