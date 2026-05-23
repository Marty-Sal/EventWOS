using EventWOS.Application.Auth.Commands;
using FluentValidation;

namespace EventWOS.Application.Auth.Validators;

public sealed class RequestOtpValidator : AbstractValidator<RequestOtpCommand>
{
    public RequestOtpValidator()
    {
        RuleFor(x => x.Mobile)
            .NotEmpty().WithMessage("Mobile number is required.")
            .Matches(@"^\+?[1-9]\d{9,14}$").WithMessage("Mobile number must be a valid international format (e.g. +919876543210).");
    }
}
