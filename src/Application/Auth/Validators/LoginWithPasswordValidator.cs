using EventWOS.Application.Auth.Commands;
using FluentValidation;

namespace EventWOS.Application.Auth.Validators;

public sealed class LoginWithPasswordValidator : AbstractValidator<LoginWithPasswordCommand>
{
    public LoginWithPasswordValidator()
    {
        RuleFor(x => x.UsernameOrEmail).NotEmpty().MaximumLength(255);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Portal).NotEmpty()
            .Must(p => p is "Admin" or "Vendor" or "Crew")
            .WithMessage("Portal must be Admin, Vendor, or Crew.");
    }
}
