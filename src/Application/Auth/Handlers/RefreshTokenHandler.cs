using EventWOS.Application.Auth.Commands;
using EventWOS.Application.Auth.Interfaces;
using EventWOS.Domain.Entities;
using EventWOS.Domain.Enums;
using EventWOS.Domain.Interfaces;
using EventWOS.Application.Interfaces;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EventWOS.Application.Auth.Handlers;

/// <summary>
/// Refresh token rotation handler:
/// 1. Hash incoming token, find matching active record
/// 2. Revoke old token (with replacement chain reference)
/// 3. Issue new access + refresh token pair
/// 4. Update session activity
/// </summary>
public sealed class RefreshTokenHandler : IRequestHandler<RefreshTokenCommand, Result<AuthResponse>>
{
    private readonly IAppDbContext _db;
    private readonly IJwtService _jwtService;
    private readonly IPermissionService _permissionService;
    private readonly IUnitOfWork _uow;
    private readonly IAuditLogger _audit;
    private readonly ILogger<RefreshTokenHandler> _logger;

    public RefreshTokenHandler(
        IAppDbContext db,
        IJwtService jwtService,
        IPermissionService permissionService,
        IUnitOfWork uow,
        IAuditLogger audit,
        ILogger<RefreshTokenHandler> logger)
    {
        _db = db;
        _jwtService = jwtService;
        _permissionService = permissionService;
        _uow = uow;
        _audit = audit;
        _logger = logger;
    }

    public async Task<Result<AuthResponse>> Handle(
        RefreshTokenCommand request,
        CancellationToken cancellationToken)
    {
        // 1. Hash the incoming token to find in DB
        var tokenHash = ComputeSha256(request.RefreshToken);

        var existing = await _db.RefreshTokens
            .Include(r => r.User)
            .FirstOrDefaultAsync(r => r.TokenHash == tokenHash, cancellationToken);

        if (existing is null || !existing.IsActive)
        {
            _logger.LogWarning("Invalid or expired refresh token attempt from IP: {IP}", request.IpAddress);
            return Result.Failure<AuthResponse>(Error.InvalidRefreshToken);
        }

        var user = existing.User;
        if (user.Status != UserStatus.Active)
            return Result.Failure<AuthResponse>(Error.AccountSuspended);

        // 2. Generate new token pair
        var (rawNew, hashNew) = _jwtService.GenerateRefreshToken();
        var newExpiry = DateTime.UtcNow.AddDays(30);
        var sessionId = Guid.NewGuid();

        // Always bust cache on token refresh — ensures any new permissions added since
        // the last login are embedded in the freshly issued access token.
        await _permissionService.InvalidateCacheForUserAsync(user.Id, cancellationToken);
        var permissions = await _permissionService.GetEffectivePermissionsAsync(
            user.Id, user.Role, cancellationToken);

        var accessToken = _jwtService.GenerateAccessToken(user, sessionId, permissions);

        // 3. Revoke old, add new
        existing.Revoke("rotated", hashNew);

        var newToken = new RefreshToken(
            user.Id,
            hashNew,
            request.DeviceId ?? existing.DeviceId,
            request.IpAddress ?? existing.IpAddress,
            newExpiry);

        _db.RefreshTokens.Add(newToken);

        // 4. Point the session row at the NEW session_id.
        //
        // This used to call UpdateActivity() only, leaving SessionId pointed at
        // the just-revoked value. The auth middleware checks the access token's
        // session_id claim against UserSessions.SessionId+IsActive on every
        // request -- so the freshly minted token (carrying the new sessionId)
        // matched no row and was rejected as "revoked" on its very next use.
        // The client treated that bogus 401 as "your session ended", cleared
        // local storage, and forced a brand-new login -- abandoning this row
        // still marked IsActive=true forever instead of terminating it. Repeat
        // that every ~60 minutes and you get exactly what showed up in
        // production: dozens of "active" sessions for the same account that
        // were each actually dead within an hour of being created.
        //
        // If no matching row exists (e.g. this device's session already ended,
        // or DeviceId wasn't supplied on the very first refresh), create one --
        // a valid, trackable token must never end up with no session backing it.
        var deviceId = request.DeviceId ?? existing.DeviceId;
        var session = await _db.UserSessions
            .FirstOrDefaultAsync(s => s.UserId == user.Id && s.DeviceId == deviceId && s.IsActive, cancellationToken);

        if (session is not null)
        {
            session.RotateSessionId(sessionId);
        }
        else
        {
            _db.UserSessions.Add(new UserSession(
                user.Id, sessionId, deviceId, "Unknown Device",
                request.IpAddress ?? existing.IpAddress, "unknown"));
        }

        await _uow.SaveChangesAsync(cancellationToken);

        await _audit.LogAsync(
            AuditAction.TokenRefreshed,
            nameof(User),
            user.Id.ToString(),
            additionalData: $"IP:{request.IpAddress}",
            cancellationToken: cancellationToken);

        return Result.Success(new AuthResponse(
            accessToken,
            rawNew,
            DateTime.UtcNow.AddMinutes(60),
            newExpiry,
            sessionId,
            new UserAuthInfo(user.Id, user.Mobile, user.FullName, user.Role.ToString(), permissions)));
    }

    private static string ComputeSha256(string input)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
