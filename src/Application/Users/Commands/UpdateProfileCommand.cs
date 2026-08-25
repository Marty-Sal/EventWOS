using EventOpsOracle.Application.Common;
using EventOpsOracle.Domain.Entities;
using EventOpsOracle.Domain.Enums;
using EventOpsOracle.Domain.Interfaces;
using EventOpsOracle.Application.Interfaces;
using EventOpsOracle.Shared.Result;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EventOpsOracle.Application.Users.Commands;

public sealed record UpdateProfileCommand(
    Guid UserId,
    string FullName,
    string? Email,
    string? AvatarUrl,
    string? InviteMessageTemplate = null,
    // ── Extended profile — same fields collected at self-registration,
    // now also editable post-signup (in particular by directly-added
    // Vendors/Crew who never had a registration form to fill these in). ──
    DateTime? DateOfBirth       = null,
    string?   City              = null,
    string?   State             = null,
    string?   Address           = null,
    string?   Bio               = null,
    string?   Skills            = null,   // Crew only
    int?      ExperienceYears   = null,   // Crew only
    string?   BusinessName      = null,   // Vendor only
    string?   ContactPersonName = null,   // Vendor only
    string?   GstNumber         = null,   // Vendor only
    string?   Website           = null    // Vendor only
) : IRequest<Result>;

public sealed class UpdateProfileValidator : AbstractValidator<UpdateProfileCommand>
{
    private const int MinimumAge = 18;
    private const int MaximumAge = 70;

    public UpdateProfileValidator()
    {
        RuleFor(x => x.FullName)
            .NotEmpty().WithMessage("Full name is required.")
            .MaximumLength(100).WithMessage("Full name cannot exceed 100 characters.");

        RuleFor(x => x.Email)
            .EmailAddress().WithMessage("Email must be a valid RFC email address.")
            .When(x => !string.IsNullOrWhiteSpace(x.Email));

        // Vendor-only field, but harmless to validate length regardless of role —
        // the column cap is 500 (see UserConfiguration).
        RuleFor(x => x.InviteMessageTemplate)
            .MaximumLength(500).WithMessage("Invite message cannot exceed 500 characters.");

        // Same 18–70 rule RegisterCrewValidator enforces at signup — applies
        // whenever a DOB is supplied here (Crew filling it in post-invite).
        RuleFor(x => x.DateOfBirth!.Value)
            .Must(dob => dob.Date <= DateTime.UtcNow.Date)
            .WithMessage("Date of birth cannot be in the future.")
            .Must(dob => User.CalculateAge(dob, DateTime.UtcNow.Date) >= MinimumAge)
            .WithMessage($"You must be at least {MinimumAge} years old.")
            .Must(dob => User.CalculateAge(dob, DateTime.UtcNow.Date) < MaximumAge)
            .WithMessage($"Age must be below {MaximumAge} years.")
            .When(x => x.DateOfBirth.HasValue);

        RuleFor(x => x.City).MaximumLength(100);
        RuleFor(x => x.State).MaximumLength(100);
        RuleFor(x => x.Address).MaximumLength(500);
        RuleFor(x => x.Bio).MaximumLength(2000);
        RuleFor(x => x.Skills).MaximumLength(500);
        RuleFor(x => x.ExperienceYears).InclusiveBetween(0, 60)
            .When(x => x.ExperienceYears.HasValue)
            .WithMessage("Experience must be between 0 and 60 years.");
        RuleFor(x => x.BusinessName).MaximumLength(200);
        RuleFor(x => x.ContactPersonName).MaximumLength(150);
        RuleFor(x => x.GstNumber).MaximumLength(50);
        RuleFor(x => x.Website).MaximumLength(255);
    }
}

public sealed class UpdateProfileHandler : IRequestHandler<UpdateProfileCommand, Result>
{
    private readonly IAppDbContext _db;
    private readonly IUnitOfWork _uow;
    private readonly IAuditLogger _audit;
    private readonly IEmailService _email;
    private readonly ILogger<UpdateProfileHandler> _logger;

    public UpdateProfileHandler(
        IAppDbContext db, IUnitOfWork uow, IAuditLogger audit,
        IEmailService email, ILogger<UpdateProfileHandler> logger)
    {
        _db = db; _uow = uow; _audit = audit; _email = email; _logger = logger;
    }

    public async Task<Result> Handle(UpdateProfileCommand request, CancellationToken ct)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == request.UserId && !u.IsDeleted, ct);
        if (user is null) return Result.Failure(Error.UserNotFound);

        var oldSnapshot = new { user.FullName, user.Email, user.AvatarUrl, user.InviteMessageTemplate };
        user.UpdateProfile(request.FullName, request.Email, request.AvatarUrl);

        // Vendor-only, but stored regardless of role check here — non-vendors simply
        // never see/set it from the UI, and GetCurrentUserQuery only surfaces it for Vendor.
        if (request.InviteMessageTemplate is not null)
            user.InviteMessageTemplate = request.InviteMessageTemplate.Trim() is { Length: > 0 } trimmed ? trimmed : null;

        // ── Extended profile fields ──────────────────────────────────────
        if (request.City is not null)    user.City    = request.City.Trim()    is { Length: > 0 } c ? c : null;
        if (request.State is not null)   user.State   = request.State.Trim()   is { Length: > 0 } s ? s : null;
        if (request.Address is not null) user.Address = request.Address.Trim() is { Length: > 0 } a ? a : null;
        if (request.Bio is not null)     user.Bio     = request.Bio.Trim()     is { Length: > 0 } b ? b : null;
        if (request.DateOfBirth.HasValue && user.Role == UserRole.Crew)
            user.SetDateOfBirth(request.DateOfBirth.Value);

        if (user.Role == UserRole.Crew)
        {
            if (request.Skills is not null) user.Skills = request.Skills.Trim() is { Length: > 0 } sk ? sk : null;
            if (request.ExperienceYears.HasValue) user.ExperienceYears = request.ExperienceYears;
        }
        if (user.Role == UserRole.Vendor)
        {
            if (request.BusinessName is not null)      user.BusinessName      = request.BusinessName.Trim()      is { Length: > 0 } bn ? bn : null;
            if (request.ContactPersonName is not null) user.ContactPersonName = request.ContactPersonName.Trim() is { Length: > 0 } cp ? cp : null;
            if (request.GstNumber is not null)         user.GstNumber         = request.GstNumber.Trim()         is { Length: > 0 } gs ? gs : null;
            if (request.Website is not null)           user.Website           = request.Website.Trim()           is { Length: > 0 } wb ? wb : null;
        }

        // First time a directly-added Vendor/Crew saves their profile —
        // notify whoever added them. Idempotent: only fires once per user.
        var justCompleted = user.MarkProfileCompletedIfFirstTime();

        await _uow.SaveChangesAsync(ct);

        await _audit.LogAsync(AuditAction.UserUpdated, "User", user.Id.ToString(),
            oldValues: oldSnapshot,
            newValues: new { request.FullName, request.Email, request.AvatarUrl, request.InviteMessageTemplate },
            cancellationToken: ct);

        if (justCompleted && user.InvitedByUserId.HasValue)
            await NotifyInviterAsync(user, ct);

        return Result.Success();
    }

    private async Task NotifyInviterAsync(User user, CancellationToken ct)
    {
        try
        {
            var inviter = await _db.Users.AsNoTracking()
                .Where(u => u.Id == user.InvitedByUserId!.Value)
                .Select(u => new { u.FullName, u.Email })
                .FirstOrDefaultAsync(ct);

            if (inviter is null || string.IsNullOrEmpty(inviter.Email)) return;

            await _email.SendProfileCompletedEmailAsync(
                inviter.Email, inviter.FullName, user.FullName, user.Role.ToString(), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Profile-completed notification failed for {UserId}.", user.Id);
        }
    }
}
