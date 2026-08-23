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
/// Vendor self-registration. Account starts in Pending status and cannot
/// log in until Admin/Manager approves via the approval queue.
///
/// Three guard layers up-front:
///   1. Username uniqueness (lowercase compared)
///   2. Mobile / email uniqueness across all non-rejected accounts
///   3. 24-hour cool-down: any Rejected row with same mobile/email
///      whose RejectedAt is within the last 24h blocks re-registration.
/// </summary>
public sealed class RegisterVendorHandler : IRequestHandler<RegisterVendorCommand, Result<RegistrationResponse>>
{
    private readonly IAppDbContext _db;
    private readonly IPasswordHasher _hasher;
    private readonly IFileUploadStorer _fileStorer;
    private readonly IUnitOfWork _uow;
    private readonly IAuditLogger _audit;
    private readonly INotificationPusher _push;
    private readonly ILogger<RegisterVendorHandler> _logger;
    private static readonly TimeSpan CoolDown = TimeSpan.FromHours(24);

    public RegisterVendorHandler(
        IAppDbContext db, IPasswordHasher hasher, IFileUploadStorer fileStorer, IUnitOfWork uow,
        IAuditLogger audit, INotificationPusher push, ILogger<RegisterVendorHandler> logger)
    {
        _db = db; _hasher = hasher; _fileStorer = fileStorer; _uow = uow; _audit = audit;
        _push = push; _logger = logger;
    }

    public async Task<Result<RegistrationResponse>> Handle(RegisterVendorCommand req, CancellationToken ct)
    {
        var usernameLower = req.Username.Trim().ToLowerInvariant();
        var emailLower    = req.Email.Trim().ToLowerInvariant();
        var mobile        = req.Mobile.Trim();

        // 1. Cool-down check — Rejected row in the last 24h with same mobile/email.
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

        // 1b. Terms & Conditions — if Admin has published one for Vendor,
        // the submitted version must match the current one exactly (client
        // fetches it fresh right before showing the checkbox; a mismatch
        // means it changed mid-fill and the user must review again).
        var currentTerms = await _db.TermsAndConditions
            .Where(t => t.Audience == TermsAudience.Vendor)
            .OrderByDescending(t => t.Version)
            .FirstOrDefaultAsync(ct);
        if (currentTerms is not null && (!req.TermsAccepted || req.TermsVersion != currentTerms.Version))
            return Result.Failure<RegistrationResponse>(Error.Custom(
                "Registration.TermsRequired",
                "Please review and accept the latest Terms & Conditions before registering."));

        // 2. Active uniqueness checks.
        var usernameTaken = await _db.Users.AnyAsync(u => u.Username == usernameLower, ct);
        if (usernameTaken)
            return Result.Failure<RegistrationResponse>(Error.Custom("Registration.UsernameTaken", "That username is already taken."));

        var mobileTaken = await _db.Users.AnyAsync(u => u.Mobile == mobile, ct);
        if (mobileTaken)
            return Result.Failure<RegistrationResponse>(Error.Custom("Registration.MobileTaken", "An account already exists with this mobile number."));

        var emailTaken = await _db.Users.AnyAsync(u => u.Email == emailLower, ct);
        if (emailTaken)
            return Result.Failure<RegistrationResponse>(Error.Custom("Registration.EmailTaken", "An account already exists with this email."));

        // 3. Create the Pending user.
        var hash = _hasher.Hash(req.Password);
        var user = User.SelfRegisterVendor(
            username:          usernameLower,
            mobile:            mobile,
            email:             emailLower,
            fullName:          req.FullName.Trim(),
            passwordHash:      hash,
            businessName:      req.BusinessName.Trim(),
            contactPersonName: req.ContactPersonName?.Trim(),
            gstNumber:         req.GstNumber?.Trim(),
            address:           req.Address?.Trim(),
            city:              req.City?.Trim(),
            state:             req.State?.Trim(),
            website:           req.Website?.Trim(),
            bio:               req.Bio?.Trim());

        // Profile photo is optional — a failure here does NOT block registration,
        // same rationale as Crew's optional photo: nice-to-have, not a compliance
        // document. Logged and swallowed rather than failing the whole signup.
        if (req.ProfilePhoto is not null)
        {
            var photoResult = await _fileStorer.StoreAsync(
                user.Id, entityId: null, DocumentType.VendorProfilePhoto,
                req.ProfilePhoto.Content, req.ProfilePhoto.FileName, req.ProfilePhoto.ContentType, ct);
            if (photoResult.IsFailure)
                _logger.LogWarning("Vendor self-registration profile photo rejected for {Username}: {Error} — continuing without it.",
                    usernameLower, photoResult.Error.Message);
        }

        _db.Users.Add(user);

        // Record T&C acceptance against the new user's own Id — safe before
        // SaveChanges the same way the profile-photo storage call above is;
        // both land in the same transaction.
        if (currentTerms is not null)
            _db.TermsAcceptances.Add(new TermsAcceptance(user.Id, TermsAudience.Vendor, currentTerms.Version));

        await _uow.SaveChangesAsync(ct);

        await _audit.LogAsync(AuditAction.UserCreated, nameof(User), user.Id.ToString(),
            additionalData: $"SelfRegister:Vendor", cancellationToken: ct);
        _logger.LogInformation("Vendor self-registered: {UserId} ({Username})", user.Id, usernameLower);

        // Tell the approval side that something arrived. Nothing on either
        // self-registration path used to push anything at all, so a new signup
        // sat in the queue completely silently until an admin happened to open
        // the page and refresh -- which is why the 2026-08-23 vendor signup
        // produced no notification. The queue badge is fetched, never pushed.
        //
        // Best-effort by design: the registration is already committed at this
        // point, so a hub hiccup must not turn a successful signup into an
        // error for the user.
        try
        {
            var pushPayload = new
            {
                UserId       = user.Id,
                PersonName   = user.FullName,
                Role         = "Vendor",
                BusinessName = user.BusinessName,
                SubmittedAt  = DateTime.UtcNow
            };
            await _push.PushToRoleAsync("Admin",   "RegistrationSubmitted", pushPayload, ct);
            await _push.PushToRoleAsync("Manager", "RegistrationSubmitted", pushPayload, ct);
        }
        catch (Exception pushEx)
        {
            _logger.LogWarning(pushEx,
                "Vendor registration {UserId} committed, but the RegistrationSubmitted push failed.", user.Id);
        }

        return Result.Success(new RegistrationResponse(
            user.Id, user.Status.ToString(),
            "Registration submitted. An administrator will review and notify you by email."));
    }
}
