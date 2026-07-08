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
/// Handles OTP verification:
/// 1. Loads and validates OTP request
/// 2. Verifies hash
/// 3. Loads or creates user
/// 4. Issues JWT + refresh token
/// 5. Creates session
/// 6. Dispatches domain events
/// </summary>
public sealed class VerifyOtpHandler : IRequestHandler<VerifyOtpCommand, Result<AuthResponse>>
{
    private readonly IAppDbContext _db;
    private readonly IOtpService _otpService;
    private readonly IJwtService _jwtService;
    private readonly IPermissionService _permissionService;
    private readonly IUnitOfWork _uow;
    private readonly IAuditLogger _audit;
    private readonly ILogger<VerifyOtpHandler> _logger;

    public VerifyOtpHandler(
        IAppDbContext db,
        IOtpService otpService,
        IJwtService jwtService,
        IPermissionService permissionService,
        IUnitOfWork uow,
        IAuditLogger audit,
        ILogger<VerifyOtpHandler> logger)
    {
        _db = db;
        _otpService = otpService;
        _jwtService = jwtService;
        _permissionService = permissionService;
        _uow = uow;
        _audit = audit;
        _logger = logger;
    }

    public async Task<Result<AuthResponse>> Handle(
        VerifyOtpCommand request,
        CancellationToken cancellationToken)
    {
        // 1. Load OTP request
        var otpRequest = await _db.OtpRequests
            .FirstOrDefaultAsync(o =>
                o.Id == request.OtpRequestId &&
                o.Mobile == request.Mobile, cancellationToken);

        if (otpRequest is null)
            return Result.Failure<AuthResponse>(Error.InvalidOtp);

        // 2. Check expiry/status
        if (!otpRequest.IsValid)
        {
            _logger.LogWarning("OTP invalid/expired for {Mobile}", request.Mobile);
            return Result.Failure<AuthResponse>(
                otpRequest.IsExpired ? Error.OtpExpired : Error.InvalidOtp);
        }

        if (otpRequest.MaxAttemptsReached)
            return Result.Failure<AuthResponse>(Error.OtpMaxAttempts);

        // 3. Verify hash
        if (!_otpService.VerifyOtp(request.Otp, otpRequest.HashedOtp))
        {
            otpRequest.MarkFailed();

            // Also update user failed attempts
            var lockedUser = await _db.Users
                .FirstOrDefaultAsync(u => u.Mobile == request.Mobile && !u.IsDeleted, cancellationToken);
            lockedUser?.RecordFailedOtpAttempt();

            await _uow.SaveChangesAsync(cancellationToken);

            await _audit.LogAsync(AuditAction.OtpFailed, nameof(OtpRequest),
                otpRequest.Id.ToString(),
                additionalData: $"IP:{request.IpAddress}",
                cancellationToken: cancellationToken);

            return Result.Failure<AuthResponse>(Error.InvalidOtp);
        }

        // 4. Mark OTP verified
        otpRequest.MarkVerified();

        // 5. Load or create user
        var user = await _db.Users
            .FirstOrDefaultAsync(u => u.Mobile == request.Mobile && !u.IsDeleted, cancellationToken);

        if (user is null)
        {
            // Auto-create Crew user on first login (can be promoted later)
            user = new User(request.Mobile, request.Mobile, UserRole.Crew);
            _db.Users.Add(user);
        }

        if (user.Status == UserStatus.Suspended)
            return Result.Failure<AuthResponse>(Error.AccountSuspended);

        user.Activate();
        user.UpdateLoginMetadata(request.IpAddress ?? string.Empty, request.DeviceId);
        user.ResetFailedAttempts();

        // 6. Invalidate any stale Redis permission cache, then read fresh from DB.
        // This ensures newly seeded/updated permissions are always reflected on login —
        // even if the user re-logs within the 5-minute cache window.
        await _permissionService.InvalidateCacheForUserAsync(user.Id, cancellationToken);
        var permissions = await _permissionService.GetEffectivePermissionsAsync(
            user.Id, user.Role, cancellationToken);

        // 7. Generate tokens
        var sessionId = Guid.NewGuid();
        var accessToken = _jwtService.GenerateAccessToken(user, sessionId, permissions);
        var (rawRefreshToken, refreshTokenHash) = _jwtService.GenerateRefreshToken();
        var refreshExpiry = DateTime.UtcNow.AddDays(30);

        var refreshToken = new RefreshToken(
            user.Id,
            refreshTokenHash,
            request.DeviceId ?? "unknown",
            request.IpAddress ?? "unknown",
            refreshExpiry);

        _db.RefreshTokens.Add(refreshToken);

        // 8. Create session
        var session = new UserSession(
            user.Id,
            sessionId,
            request.DeviceId ?? "unknown",
            request.DeviceName ?? "Unknown Device",
            request.IpAddress ?? "unknown",
            request.UserAgent ?? "unknown");

        _db.UserSessions.Add(session);

        await _uow.SaveChangesAsync(cancellationToken);

        // OTP verify is a login event — no bearer token on the request
        // yet, so ICurrentUser.UserId is null. Pass user.Id explicitly
        // so the audit trail correctly attributes the login instead of
        // showing "System" in the log UI. Matches LoginWithPasswordHandler.
        await _audit.LogAsync(
            AuditAction.Login,
            nameof(User),
            user.Id.ToString(),
            additionalData: $"SessionId:{sessionId},IP:{request.IpAddress}",
            actorUserId: user.Id,
            cancellationToken: cancellationToken);

        _logger.LogInformation("User {UserId} logged in via OTP. SessionId: {SessionId}", user.Id, sessionId);

        return Result.Success(new AuthResponse(
            accessToken,
            rawRefreshToken,
            DateTime.UtcNow.AddMinutes(60),
            refreshExpiry,
            sessionId,
            new UserAuthInfo(user.Id, user.Mobile, user.FullName, user.Role.ToString(), permissions)));
    }
}
