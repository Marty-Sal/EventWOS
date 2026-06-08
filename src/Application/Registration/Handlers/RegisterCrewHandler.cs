using EventWOS.Application.Auth.Interfaces;
using EventWOS.Application.Interfaces;
using EventWOS.Application.Registration.Commands;
using EventWOS.Domain.Entities;
using EventWOS.Domain.Enums;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EventWOS.Application.Registration.Handlers;

/// <summary>
/// Crew self-registration. Mirror of RegisterVendorHandler but resolves
/// the optional ReferralCode against an Active Vendor and stamps VendorId
/// on the crew record. Cool-down rules identical.
/// </summary>
public sealed class RegisterCrewHandler : IRequestHandler<RegisterCrewCommand, Result<RegistrationResponse>>
{
    private readonly IAppDbContext _db;
    private readonly IPasswordHasher _hasher;
    private readonly IUnitOfWork _uow;
    private readonly IAuditLogger _audit;
    private readonly ILogger<RegisterCrewHandler> _logger;
    private static readonly TimeSpan CoolDown = TimeSpan.FromHours(24);

    public RegisterCrewHandler(
        IAppDbContext db, IPasswordHasher hasher, IUnitOfWork uow,
        IAuditLogger audit, ILogger<RegisterCrewHandler> logger)
    {
        _db = db; _hasher = hasher; _uow = uow; _audit = audit; _logger = logger;
    }

    public async Task<Result<RegistrationResponse>> Handle(RegisterCrewCommand req, CancellationToken ct)
    {
        var usernameLower = req.Username.Trim().ToLowerInvariant();
        var emailLower    = req.Email.Trim().ToLowerInvariant();
        var mobile        = req.Mobile.Trim();
        var refCode       = req.ReferralCode?.Trim().ToUpperInvariant();

        // 1. Cool-down check.
        var coolDownCutoff = DateTime.UtcNow - CoolDown;
        var blocked = await _db.Users.IgnoreQueryFilters()
            .Where(u => u.Status == UserStatus.Rejected
                     && u.RejectedAt != null && u.RejectedAt > coolDownCutoff
                     && (u.Mobile == mobile || u.Email == emailLower))
            .OrderByDescending(u => u.RejectedAt)
            .Select(u => new { u.RejectedAt })
            .FirstOrDefaultAsync(ct);
        if (blocked is not null)
        {
            var canRetry = blocked.RejectedAt!.Value + CoolDown;
            return Result.Failure<RegistrationResponse>(Error.Custom(
                "Registration.CoolDown",
                $"This contact was rejected recently. You can register again after {canRetry:dd MMM yyyy, HH:mm} UTC."));
        }

        // 2. Uniqueness.
        if (await _db.Users.AnyAsync(u => u.Username == usernameLower, ct))
            return Result.Failure<RegistrationResponse>(Error.Custom("Registration.UsernameTaken", "That username is already taken."));
        if (await _db.Users.AnyAsync(u => u.Mobile == mobile, ct))
            return Result.Failure<RegistrationResponse>(Error.Custom("Registration.MobileTaken", "An account already exists with this mobile number."));
        if (await _db.Users.AnyAsync(u => u.Email == emailLower, ct))
            return Result.Failure<RegistrationResponse>(Error.Custom("Registration.EmailTaken", "An account already exists with this email."));

        // 3. Resolve referral code (optional). Must point to an Active Vendor.
        Guid? resolvedVendorId = null;
        if (!string.IsNullOrEmpty(refCode))
        {
            var vendor = await _db.Users
                .Where(u => u.Role == UserRole.Vendor
                         && u.Status == UserStatus.Active
                         && u.ReferralCode == refCode)
                .Select(u => new { u.Id })
                .FirstOrDefaultAsync(ct);
            if (vendor is null)
                return Result.Failure<RegistrationResponse>(Error.Custom("Registration.InvalidReferral", "That referral code is not valid."));
            resolvedVendorId = vendor.Id;
        }

        // 4. Create the Pending crew user.
        var hash = _hasher.Hash(req.Password);
        var user = User.SelfRegisterCrew(
            username:         usernameLower,
            mobile:           mobile,
            email:            emailLower,
            fullName:         req.FullName.Trim(),
            passwordHash:     hash,
            referralCodeUsed: refCode,
            city:             req.City?.Trim(),
            skills:           req.Skills?.Trim(),
            experienceYears:  req.ExperienceYears,
            bio:              req.Bio?.Trim());
        if (resolvedVendorId.HasValue)
            user.JoinVendor(resolvedVendorId.Value);

        _db.Users.Add(user);
        await _uow.SaveChangesAsync(ct);

        await _audit.LogAsync(AuditAction.UserCreated, nameof(User), user.Id.ToString(),
            additionalData: $"SelfRegister:Crew Referral:{refCode ?? "(none)"}", cancellationToken: ct);
        _logger.LogInformation("Crew self-registered: {UserId} ({Username}) vendor={Vendor}", user.Id, usernameLower, resolvedVendorId);

        return Result.Success(new RegistrationResponse(
            user.Id, user.Status.ToString(),
            "Registration submitted. An administrator will review and notify you by email."));
    }
}
