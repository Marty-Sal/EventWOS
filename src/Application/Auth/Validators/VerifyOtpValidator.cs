using EventWOS.Application.Auth.Commands;
using FluentValidation;

namespace EventWOS.Application.Auth.Validators;

public sealed class VerifyOtpValidator : AbstractValidator<VerifyOtpCommand>
{
    public VerifyOtpValidator()
    {
        RuleFor(x => x.Mobile)
            .NotEmpty().WithMessage("Mobile number is required.")
            .Matches(@"^\+?[1-9]\d{9,14}$").WithMessage("Invalid mobile number format.");

        RuleFor(x => x.Otp)
            .NotEmpty().WithMessage("OTP is required.")
            .Length(6).WithMessage("OTP must be exactly 6 digits.")
            .Matches(@"^\d{6}$").WithMessage("OTP must contain only digits.");

        RuleFor(x => x.OtpRequestId)
            .NotEmpty().WithMessage("OTP request ID is required.");
    }
}
