using EventOpsOracle.Application.Vendors.Commands;
using FluentValidation;

namespace EventOpsOracle.Application.Vendors.Validators;

/// <summary>
/// Server-side authority for the "Add Vendor" admin flow (Vendors.razor /
/// Users.razor create-user modal). Mirrors ClientValidation.cs on the
/// Blazor client — that copy is a UX nicety only; this is what actually
/// gates the write. Business name is deliberately left unconstrained
/// (free-text business detail, not an identity field).
/// </summary>
public sealed class CreateVendorValidator : AbstractValidator<CreateVendorCommand>
{
    public CreateVendorValidator()
    {
        RuleFor(x => x.Mobile).NotEmpty().Matches(@"^\d{10}$")
            .WithMessage("Mobile number must be exactly 10 digits.");

        RuleFor(x => x.FullName).NotEmpty().MaximumLength(100)
            .Matches(@"^[A-Za-z ]+$")
            .WithMessage("Full name can only contain letters and spaces.");

        RuleFor(x => x.Email).NotEmpty().EmailAddress()
            .WithMessage("A valid email address is required.");

        RuleFor(x => x.BusinessName).MaximumLength(150);
    }
}
