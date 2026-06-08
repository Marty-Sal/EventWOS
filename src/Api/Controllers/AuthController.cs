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
        {
            // Surface the reason so the client can show the right message:
            // suspended account = 'inactive', anything else = 'expired'
            // (revoked sessions never get this far — they're caught by the
            //  JWT validation pipeline on subsequent requests).
            var reason = result.Error.Code == "Auth.AccountSuspended" ? "inactive" : "expired";
            if (!Response.Headers.ContainsKey("X-Auth-Fail-Reason"))
                Response.Headers.Append("X-Auth-Fail-Reason", reason);
            return Unauthorized(ApiResponse<AuthResponse>.Fail(result.Error.Message));
        }

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

    // ─── Password-based login + reset (new) ───────────────────────────────

    /// <summary>Username/email + password login. Portal: "Admin" | "Vendor" | "Crew".</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<PasswordLoginResponse>), 200)]
    public async Task<IActionResult> LoginWithPassword(
        [FromBody] LoginDto dto, CancellationToken ct)
    {
        var cmd = new LoginWithPasswordCommand(
            dto.UsernameOrEmail, dto.Password, dto.Portal,
            dto.DeviceId, dto.DeviceName,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            Request.Headers.UserAgent);

        var result = await _mediator.Send(cmd, ct);
        if (result.IsFailure)
        {
            var status = result.Error.Code switch
            {
                "Auth.AccountLocked"    => 423,
                "Auth.AccountSuspended" => 403,
                "Auth.PendingApproval"  => 403,
                "Auth.Rejected"         => 403,
                "Auth.WrongPortal"      => 403,
                _ => 401
            };
            return StatusCode(status, ApiResponse<PasswordLoginResponse>.Fail(result.Error.Message));
        }
        return Ok(ApiResponse<PasswordLoginResponse>.Ok(result.Value));
    }

    /// <summary>Step 1 of forgot-password — sends OTP via SMS + email.</summary>
    [HttpPost("forgot-password/request")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<RequestPasswordResetResponse>), 200)]
    public async Task<IActionResult> RequestPasswordReset(
        [FromBody] ForgotPasswordRequestDto dto, CancellationToken ct)
    {
        var cmd = new RequestPasswordResetCommand(
            dto.UsernameEmailOrMobile,
            HttpContext.Connection.RemoteIpAddress?.ToString());
        var result = await _mediator.Send(cmd, ct);
        return Ok(ApiResponse<RequestPasswordResetResponse>.Ok(result.Value));
    }

    /// <summary>Step 2 of forgot-password — verify OTP + set new password.</summary>
    [HttpPost("forgot-password/reset")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse), 200)]
    public async Task<IActionResult> ResetPassword(
        [FromBody] ResetPasswordDto dto, CancellationToken ct)
    {
        var cmd = new ResetPasswordCommand(dto.OtpRequestId, dto.Mobile, dto.Otp, dto.NewPassword);
        var result = await _mediator.Send(cmd, ct);
        return result.IsSuccess
            ? Ok(ApiResponse.Ok("Password updated. Please sign in."))
            : BadRequest(ApiResponse.Fail(result.Error.Message));
    }

    /// <summary>First-login setup for grandfathered users. Sets username + password after OTP.</summary>
    [HttpPost("setup-password")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse), 200)]
    public async Task<IActionResult> SetupPassword(
        [FromBody] SetupPasswordDto dto, CancellationToken ct)
    {
        var cmd = new SetupPasswordCommand(dto.OtpRequestId, dto.Mobile, dto.Otp, dto.NewUsername, dto.NewPassword);
        var result = await _mediator.Send(cmd, ct);
        return result.IsSuccess
            ? Ok(ApiResponse.Ok("Account set up. Please sign in."))
            : BadRequest(ApiResponse.Fail(result.Error.Message));
    }
}

// ─── Request DTOs ──────────────────────────────────────────────────────────
public sealed record RequestOtpDto(string Mobile, string? DeviceId);
public sealed record VerifyOtpDto(string Mobile, string Otp, Guid OtpRequestId, string? DeviceId, string? DeviceName);
public sealed record RefreshDto(string RefreshToken, string? DeviceId);
public sealed record LogoutDto(string RefreshToken);
public sealed record LoginDto(string UsernameOrEmail, string Password, string Portal, string? DeviceId, string? DeviceName);
public sealed record ForgotPasswordRequestDto(string UsernameEmailOrMobile);
public sealed record ResetPasswordDto(Guid OtpRequestId, string Mobile, string Otp, string NewPassword);
public sealed record SetupPasswordDto(Guid OtpRequestId, string Mobile, string Otp, string NewUsername, string NewPassword);
