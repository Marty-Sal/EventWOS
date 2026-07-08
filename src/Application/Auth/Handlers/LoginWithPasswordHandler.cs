using EventWOS.Application.Auth.Commands;
using EventWOS.Application.Auth.Interfaces;
using EventWOS.Application.Interfaces;
using EventWOS.Domain.Entities;
using EventWOS.Domain.Enums;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EventWOS.Application.Auth.Handlers;

/// <summary>
/// Password-based login. Resolves the user by username OR email
/// (lowercased), checks the portal/role gate, verifies the BCrypt hash,
/// and issues the same AuthResponse the OTP flow returns. Reuses
/// JwtService + PermissionService + session creation so token shape is
/// identical to the OTP path.
///
/// Grandfathered users (PasswordHash null OR RequirePasswordReset true)
/// short-circuit: no token, just RequiresPasswordSetup=true so the UI
/// can route them to the setup-password page.
/// </summary>
public sealed class LoginWithPasswordHandler : IRequestHandler<LoginWithPasswordCommand, Result<PasswordLoginResponse>>
{
    private readonly IAppDbContext _db;
    private readonly IPasswordHasher _hasher;
    private readonly IJwtService _jwt;
    private readonly IPermissionService _perm;
    private readonly IUnitOfWork _uow;
    private readonly IAuditLogger _audit;
    private readonly ILogger<LoginWithPasswordHandler> _logger;

    public LoginWithPasswordHandler(
        IAppDbContext db, IPasswordHasher hasher, IJwtService jwt,
        IPermissionService perm, IUnitOfWork uow, IAuditLogger audit,
        ILogger<LoginWithPasswordHandler> logger)
    {
        _db = db; _hasher = hasher; _jwt = jwt; _perm = perm;
        _uow = uow; _audit = audit; _logger = logger;
    }

    public async Task<Result<PasswordLoginResponse>> Handle(LoginWithPasswordCommand req, CancellationToken ct)
    {
        var key = req.UsernameOrEmail.Trim().ToLowerInvariant();

        // Match on username OR email (both stored lowercased).
        var user = await _db.Users
            .FirstOrDefaultAsync(u => !u.IsDeleted
                                   && (u.Username == key || u.Email == key), ct);

        if (user is null)
            return Result.Failure<PasswordLoginResponse>(Error.Custom("Auth.InvalidCredentials", "Invalid username or password."));

        // Portal/role gate — user clicked the wrong tile.
        if (!PortalMatches(user.Role, req.Portal))
        {
            var correct = user.Role switch
            {
                UserRole.Admin or UserRole.Manager => "Admin",
                UserRole.Vendor => "Vendor",
                _ => "Crew"
            };
            return Result.Failure<PasswordLoginResponse>(Error.Custom(
                "Auth.WrongPortal",
                $"This account belongs to the {correct} portal. Please use /login/{correct.ToLowerInvariant()}."));
        }

        // Account status checks BEFORE we verify password (cheap rejects first).
        switch (user.Status)
        {
            case UserStatus.Suspended:
            case UserStatus.Deactivated:
                return Result.Failure<PasswordLoginResponse>(Error.AccountSuspended);
            case UserStatus.Pending:
                return Result.Failure<PasswordLoginResponse>(Error.Custom(
                    "Auth.PendingApproval",
                    "Your account is awaiting approval. You'll receive an email once it's approved."));
            case UserStatus.Rejected:
                return Result.Failure<PasswordLoginResponse>(Error.Custom(
                    "Auth.Rejected",
                    "Your registration was not approved."));
        }

        if (user.IsLocked)
            return Result.Failure<PasswordLoginResponse>(Error.AccountLocked);

        // Grandfathered users: no password yet, or admin-forced reset.
        if (string.IsNullOrEmpty(user.PasswordHash) || user.RequirePasswordReset)
        {
            return Result.Success(new PasswordLoginResponse(
                RequiresPasswordSetup: true,
                Mobile: user.Mobile,
                Auth: null));
        }

        // Verify password.
        if (!_hasher.Verify(req.Password, user.PasswordHash!))
        {
            user.RecordFailedLoginAttempt();
            await _uow.SaveChangesAsync(ct);
            return Result.Failure<PasswordLoginResponse>(Error.Custom("Auth.InvalidCredentials", "Invalid username or password."));
        }

        // ── Successful login — issue tokens (identical shape to OTP path).
        user.ResetLoginAttempts();
        user.UpdateLoginMetadata(req.IpAddress ?? string.Empty, req.DeviceId);

        await _perm.InvalidateCacheForUserAsync(user.Id, ct);
        var permissions = await _perm.GetEffectivePermissionsAsync(user.Id, user.Role, ct);

        var sessionId = Guid.NewGuid();
        var accessToken = _jwt.GenerateAccessToken(user, sessionId, permissions);
        var (rawRefresh, refreshHash) = _jwt.GenerateRefreshToken();
        var refreshExpiry = DateTime.UtcNow.AddDays(30);

        _db.RefreshTokens.Add(new RefreshToken(
            user.Id, refreshHash,
            req.DeviceId ?? "unknown", req.IpAddress ?? "unknown", refreshExpiry));

        _db.UserSessions.Add(new UserSession(
            user.Id, sessionId,
            req.DeviceId ?? "unknown", req.DeviceName ?? "Unknown Device",
            req.IpAddress ?? "unknown", req.UserAgent ?? "unknown"));

        await _uow.SaveChangesAsync(ct);
        // Pass user.Id as the explicit actor — the request has no bearer
        // token yet at this point (login IS the auth event), so
        // ICurrentUser.UserId is null. Without the override the audit row
        // would render as "System" in the log UI even though we know
        // exactly which user just authenticated.
        await _audit.LogAsync(AuditAction.Login, nameof(User), user.Id.ToString(),
            additionalData: $"Method:Password Session:{sessionId}",
            actorUserId: user.Id,
            cancellationToken: ct);

        _logger.LogInformation("Password login OK: {UserId} portal={Portal}", user.Id, req.Portal);

        var auth = new AuthResponse(
            accessToken, rawRefresh,
            DateTime.UtcNow.AddMinutes(60), refreshExpiry, sessionId,
            new UserAuthInfo(user.Id, user.Mobile, user.FullName, user.Role.ToString(), permissions));
        return Result.Success(new PasswordLoginResponse(false, null, auth));
    }

    private static bool PortalMatches(UserRole role, string portal) => (role, portal) switch
    {
        (UserRole.Admin,   "Admin")  => true,
        (UserRole.Manager, "Admin")  => true,   // Manager logs in via Admin portal
        (UserRole.Vendor,  "Vendor") => true,
        (UserRole.Crew,    "Crew")   => true,
        _ => false
    };
}
