using EventWOS.Application.Registration.Commands;
using FluentValidation;

namespace EventWOS.Application.Registration.Validators;

public sealed class RegisterCrewValidator : AbstractValidator<RegisterCrewCommand>
{
    public RegisterCrewValidator()
    {
        RuleFor(x => x.Username).NotEmpty().Length(3, 50)
            .Matches("^[a-zA-Z0-9_.-]+$")
            .WithMessage("Username can only contain letters, numbers, '.', '_' or '-'.");
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.Mobile).NotEmpty().Matches(@"^\+?[0-9]{10,15}$")
            .WithMessage("Mobile must be 10–15 digits, optionally prefixed with +.");
        RuleFor(x => x.Password).Must(PasswordRules.IsValid).WithMessage(PasswordRules.Description);
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.ReferralCode).MaximumLength(20);
        RuleFor(x => x.ExperienceYears).InclusiveBetween(0, 60).When(x => x.ExperienceYears.HasValue);
    }
}
