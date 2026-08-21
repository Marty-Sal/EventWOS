using EventWOS.Application.Registration.Commands;
using FluentValidation;

namespace EventWOS.Application.Registration.Validators;

public sealed class RegisterVendorValidator : AbstractValidator<RegisterVendorCommand>
{
    private const long MaxPhotoBytes = 5 * 1024 * 1024;
    private static readonly string[] AllowedPhotoTypes = { "image/jpeg", "image/png", "image/webp" };

    public RegisterVendorValidator()
    {
        RuleFor(x => x.Username).NotEmpty().Length(3, 50)
            .Matches("^[a-zA-Z0-9_.-]+$")
            .WithMessage("Username can only contain letters, numbers, '.', '_' or '-'.");
        RuleFor(x => x.Email).NotEmpty().EmailAddress();

        // Exactly 10 digits — aligned with Crew's rule (was previously a looser
        // +?[0-9]{10,15} pattern; tightened for consistency across both roles).
        RuleFor(x => x.Mobile).NotEmpty().Matches(@"^\d{10}$")
            .WithMessage("Mobile number must be exactly 10 digits.");

        RuleFor(x => x.Password).Must(PasswordRules.IsValid).WithMessage(PasswordRules.Description);

        // Letters and spaces only — aligned with Crew's rule.
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(100)
            .Matches(@"^[A-Za-z ]+$")
            .WithMessage("Full name can only contain letters and spaces.");

        RuleFor(x => x.BusinessName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.GstNumber).MaximumLength(50);
        RuleFor(x => x.City).MaximumLength(100);
        RuleFor(x => x.Website).MaximumLength(255);

        RuleFor(x => x.TermsAccepted).Equal(true)
            .WithMessage("You must accept the Terms & Conditions to register.");

        // Profile photo is optional for Vendor (unlike Crew's mandatory ID proof) —
        // every predicate is null-safe by construction, same pattern as
        // RegisterCrewValidator's IdentificationProof rule.
        RuleFor(x => x.ProfilePhoto)
            .Must(f => f is null || (f.Content is { Length: > 0 })).WithMessage("Profile photo file is empty.")
            .Must(f => f is null || f.Content is null || f.Content.LongLength <= MaxPhotoBytes)
                .WithMessage("Profile photo must not exceed 5 MB.")
            .Must(f => f is null || f.ContentType is null || AllowedPhotoTypes.Contains(f.ContentType, StringComparer.OrdinalIgnoreCase))
                .WithMessage("Profile photo must be a JPEG, PNG or WebP image.");
    }
}
