using EventWOS.Application.Auth.Commands;
using FluentValidation;

namespace EventWOS.Application.Auth.Validators;

public sealed class VerifyOtpValidator : AbstractValidator<VerifyOtpCommand>
{
    public VerifyOtpValidator()
    {
        // Same exactly-10-digits rule as RequestOtpValidator — both endpoints
        // in the OTP flow must agree on format or verify would never find
        // the OtpRequest row saved during request.
        RuleFor(x => x.Mobile).NotEmpty().Matches(@"^\d{10}$")
            .WithMessage("Mobile number must be exactly 10 digits.");

        RuleFor(x => x.Otp)
            .NotEmpty().WithMessage("OTP is required.")
            .Length(6).WithMessage("OTP must be exactly 6 digits.")
            .Matches(@"^\d{6}$").WithMessage("OTP must contain only digits.");

        RuleFor(x => x.OtpRequestId)
            .NotEmpty().WithMessage("OTP request ID is required.");
    }
}
