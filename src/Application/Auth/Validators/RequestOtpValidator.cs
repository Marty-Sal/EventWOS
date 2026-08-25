using EventOpsOracle.Application.Auth.Commands;
using FluentValidation;

namespace EventOpsOracle.Application.Auth.Validators;

public sealed class RequestOtpValidator : AbstractValidator<RequestOtpCommand>
{
    public RequestOtpValidator()
    {
        // Exactly 10 digits — no country code, no separators. Matches the
        // convention RegisterCrewValidator/RegisterVendorValidator already use
        // for Mobile, and the client-side filter in LoginOtp.razor.
        RuleFor(x => x.Mobile).NotEmpty().Matches(@"^\d{10}$")
            .WithMessage("Mobile number must be exactly 10 digits.");
    }
}
