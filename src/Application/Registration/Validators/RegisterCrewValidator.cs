using EventWOS.Application.Registration.Commands;
using EventWOS.Domain.Entities;
using FluentValidation;

namespace EventWOS.Application.Registration.Validators;

public sealed class RegisterCrewValidator : AbstractValidator<RegisterCrewCommand>
{
    private const int MinimumAge = 18;
    private const long MaxIdProofBytes = 5 * 1024 * 1024;
    private static readonly string[] AllowedIdProofTypes = { "image/jpeg", "image/png", "application/pdf" };

    public RegisterCrewValidator()
    {
        RuleFor(x => x.Username).NotEmpty().Length(3, 50)
            .Matches("^[a-zA-Z0-9_.-]+$")
            .WithMessage("Username can only contain letters, numbers, '.', '_' or '-'.");
        RuleFor(x => x.Email).NotEmpty().EmailAddress();

        // Exactly 10 digits — no country code, no separators. Client strips
        // formatting before sending; this is the authoritative server check.
        RuleFor(x => x.Mobile).NotEmpty().Matches(@"^\d{10}$")
            .WithMessage("Mobile number must be exactly 10 digits.");

        RuleFor(x => x.Password).Must(PasswordRules.IsValid).WithMessage(PasswordRules.Description);

        // Letters and spaces only — no digits/symbols. Matches the client-side check
        // in RegisterCrew.razor; both must reject the same inputs.
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(100)
            .Matches(@"^[A-Za-z ]+$")
            .WithMessage("Full name can only contain letters and spaces.");

        // 18+ only. Also rejects nonsensical dates (future, or absurdly old) so a
        // client bug/typo can't silently create an unapprovable account.
        RuleFor(x => x.DateOfBirth)
            .Must(dob => dob.Date <= DateTime.UtcNow.Date)
            .WithMessage("Date of birth cannot be in the future.")
            .Must(dob => dob.Date >= DateTime.UtcNow.Date.AddYears(-100))
            .WithMessage("Please enter a valid date of birth.")
            .Must(dob => User.CalculateAge(dob, DateTime.UtcNow.Date) >= MinimumAge)
            .WithMessage($"You must be at least {MinimumAge} years old to register.");

        // Crew self-registration MUST include a vendor referral code.
        // Direct (vendor-less) crew can only be created by Admin via the
        // CreateUser admin endpoint — never through this self-signup path.
        // See rule #28 / Phase 5 spec.
        RuleFor(x => x.ReferralCode)
            .NotEmpty().WithMessage("A vendor referral code is required to register as crew. Please ask your vendor for their code.")
            .MaximumLength(20);

        RuleFor(x => x.ExperienceYears).InclusiveBetween(0, 60).When(x => x.ExperienceYears.HasValue);

        // Identification proof is mandatory for crew (Aadhaar / driving licence / voter ID etc.).
        // Every predicate below is written null-safe on purpose (no nested property-selector
        // rules) so there's zero reliance on FluentValidation's When()-guards-the-selector
        // behaviour — a missing/malformed payload just fails validation, never throws.
        // Full magic-byte + size validation happens again in IFileUploadStorer regardless.
        RuleFor(x => x.IdentificationProof)
            .Must(f => f is not null).WithMessage("Identification proof (Aadhaar card, driving licence, or voter ID) is required.")
            .Must(f => f is null || (f.Content is { Length: > 0 })).WithMessage("Identification proof file is empty.")
            .Must(f => f is null || f.Content is null || f.Content.LongLength <= MaxIdProofBytes)
                .WithMessage("Identification proof must not exceed 5 MB.")
            .Must(f => f is null || f.ContentType is null || AllowedIdProofTypes.Contains(f.ContentType, StringComparer.OrdinalIgnoreCase))
                .WithMessage("Identification proof must be a JPEG/PNG image or a PDF.");
    }
}
