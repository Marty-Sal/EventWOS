using EventWOS.Application.Registration.Commands;
using FluentValidation;

namespace EventWOS.Application.Registration.Validators;

public sealed class RegisterVendorValidator : AbstractValidator<RegisterVendorCommand>
{
    public RegisterVendorValidator()
    {
        RuleFor(x => x.Username).NotEmpty().Length(3, 50)
            .Matches("^[a-zA-Z0-9_.-]+$")
            .WithMessage("Username can only contain letters, numbers, '.', '_' or '-'.");
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.Mobile).NotEmpty().Matches(@"^\+?[0-9]{10,15}$")
            .WithMessage("Mobile must be 10–15 digits, optionally prefixed with +.");
        RuleFor(x => x.Password).Must(PasswordRules.IsValid).WithMessage(PasswordRules.Description);
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.BusinessName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.GstNumber).MaximumLength(50);
        RuleFor(x => x.City).MaximumLength(100);
        RuleFor(x => x.Website).MaximumLength(255);
    }
}
