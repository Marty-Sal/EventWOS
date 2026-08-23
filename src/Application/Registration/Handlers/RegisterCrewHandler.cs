using EventWOS.Application.Auth.Interfaces;
using EventWOS.Application.Files;
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
///
/// Also stores the mandatory ID-proof (and optional profile photo) via
/// IFileUploadStorer. Order matters here: the User entity's Id is
/// generated in-memory the moment the object is constructed (BaseEntity),
/// so we build the User FIRST (without saving it), use its Id as the
/// FileDocument OwnerId, store the file(s), and only then SaveChangesAsync
/// once — User + FileDocument row(s) commit together in one transaction.
/// If file storage fails, nothing has hit the database yet, so there's no
/// orphaned Pending user blocking a retry on the unique mobile/email checks.
/// </summary>
public sealed class RegisterCrewHandler : IRequestHandler<RegisterCrewCommand, Result<RegistrationResponse>>
{
    private readonly IAppDbContext _db;
    private readonly IPasswordHasher _hasher;
    private readonly IFileUploadStorer _fileStorer;
    private readonly IUnitOfWork _uow;
    private readonly IAuditLogger _audit;
    private readonly INotificationPusher _push;
    private readonly ILogger<RegisterCrewHandler> _logger;
    private static readonly TimeSpan CoolDown = TimeSpan.FromHours(24);

    public RegisterCrewHandler(
        IAppDbContext db, IPasswordHasher hasher, IFileUploadStorer fileStorer, IUnitOfWork uow,
        IAuditLogger audit, INotificationPusher push, ILogger<RegisterCrewHandler> logger)
    {
        _db = db; _hasher = hasher; _fileStorer = fileStorer; _uow = uow; _audit = audit;
        _push = push; _logger = logger;
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

        // 1b. Terms & Conditions — same check as Vendor's, against the Crew audience.
        var currentTerms = await _db.TermsAndConditions
            .Where(t => t.Audience == TermsAudience.Crew)
            .OrderByDescending(t => t.Version)
            .FirstOrDefaultAsync(ct);
        if (currentTerms is not null && (!req.TermsAccepted || req.TermsVersion != currentTerms.Version))
            return Result.Failure<RegistrationResponse>(Error.Custom(
                "Registration.TermsRequired",
                "Please review and accept the latest Terms & Conditions before registering."));

        // 2. Uniqueness.
        if (await _db.Users.AnyAsync(u => u.Username == usernameLower, ct))
            return Result.Failure<RegistrationResponse>(Error.Custom("Registration.UsernameTaken", "That username is already taken."));
        if (await _db.Users.AnyAsync(u => u.Mobile == mobile, ct))
            return Result.Failure<RegistrationResponse>(Error.Custom("Registration.MobileTaken", "An account already exists with this mobile number."));
        if (await _db.Users.AnyAsync(u => u.Email == emailLower, ct))
            return Result.Failure<RegistrationResponse>(Error.Custom("Registration.EmailTaken", "An account already exists with this email."));

        // 3. Resolve referral code — MANDATORY for self-registration.
        //    Direct (vendor-less) crew exist only via the Admin CreateUser
        //    path; crew self-signup is always under a vendor's umbrella.
        //    The FluentValidation rule already blocks empty refCode, so this
        //    is a defence-in-depth check.
        if (string.IsNullOrEmpty(refCode))
            return Result.Failure<RegistrationResponse>(Error.Custom(
                "Registration.ReferralRequired",
                "A vendor referral code is required to register as crew."));

        var vendor = await _db.Users
            .Where(u => u.Role == UserRole.Vendor
                     && u.Status == UserStatus.Active
                     && u.ReferralCode == refCode)
            .Select(u => new { u.Id })
            .FirstOrDefaultAsync(ct);
        if (vendor is null)
            return Result.Failure<RegistrationResponse>(Error.Custom(
                "Registration.InvalidReferral",
                "That referral code is not valid. Please check it with your vendor."));
        Guid? resolvedVendorId = vendor.Id;

        // 4. Build the Pending crew user IN MEMORY (its Id already exists — see class doc).
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
            bio:              req.Bio?.Trim(),
            dateOfBirth:      req.DateOfBirth);
        if (resolvedVendorId.HasValue)
            user.JoinVendor(resolvedVendorId.Value);

        // 5. Store identification proof (mandatory). Validated again here (signature
        //    check included) — the FluentValidation rule is a fast-fail, this is the
        //    authority. Nothing has been saved to the DB yet, so a failure here is a
        //    clean no-op — no orphaned user row, no blocked retry.
        var idProofResult = await _fileStorer.StoreAsync(
            user.Id, entityId: null, DocumentType.CrewIdentificationProof,
            req.IdentificationProof.Content, req.IdentificationProof.FileName, req.IdentificationProof.ContentType, ct);
        if (idProofResult.IsFailure)
            return Result.Failure<RegistrationResponse>(idProofResult.Error);

        // 6. Store profile photo (optional). A failure here does NOT block registration —
        //    it's a nice-to-have, not a compliance document. Logged and swallowed.
        if (req.ProfilePhoto is not null)
        {
            var photoResult = await _fileStorer.StoreAsync(
                user.Id, entityId: null, DocumentType.CrewProfilePhoto,
                req.ProfilePhoto.Content, req.ProfilePhoto.FileName, req.ProfilePhoto.ContentType, ct);
            if (photoResult.IsFailure)
                _logger.LogWarning("Crew self-registration profile photo rejected for {Username}: {Error} — continuing without it.",
                    usernameLower, photoResult.Error.Message);
        }

        _db.Users.Add(user);

        if (currentTerms is not null)
            _db.TermsAcceptances.Add(new TermsAcceptance(user.Id, TermsAudience.Crew, currentTerms.Version));

        await _uow.SaveChangesAsync(ct);

        await _audit.LogAsync(AuditAction.UserCreated, nameof(User), user.Id.ToString(),
            additionalData: $"SelfRegister:Crew Referral:{refCode ?? "(none)"}", cancellationToken: ct);
        _logger.LogInformation("Crew self-registered: {UserId} ({Username}) vendor={Vendor}", user.Id, usernameLower, resolvedVendorId);

        // Same silent-queue problem as the vendor path (see RegisterVendorHandler),
        // with one addition: crew self-signup always happens under a vendor's
        // referral code, and it is that VENDOR who approves the crew member
        // first. So the referring vendor is notified directly as well -- their
        // dashboard "Profile Approval" card counts exactly these rows.
        try
        {
            var pushPayload = new
            {
                UserId       = user.Id,
                PersonName   = user.FullName,
                Role         = "Crew",
                SubmittedAt  = DateTime.UtcNow
            };
            await _push.PushToUserAsync(vendor.Id, "RegistrationSubmitted", pushPayload, ct);
            await _push.PushToRoleAsync("Admin",   "RegistrationSubmitted", pushPayload, ct);
            await _push.PushToRoleAsync("Manager", "RegistrationSubmitted", pushPayload, ct);
        }
        catch (Exception pushEx)
        {
            _logger.LogWarning(pushEx,
                "Crew registration {UserId} committed, but the RegistrationSubmitted push failed.", user.Id);
        }

        return Result.Success(new RegistrationResponse(
            user.Id, user.Status.ToString(),
            "Registration submitted. An administrator will review and notify you by email."));
    }
}
