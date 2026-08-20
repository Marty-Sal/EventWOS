using EventWOS.Application.Crew.Commands;
using FluentValidation;

namespace EventWOS.Application.Crew.Validators;

/// <summary>
/// Server-side authority for the "Add Crew Member" flow (Crew.razor /
/// Users.razor create-user modal) — the direct, vendor-linked crew-creation
/// path used by Admin/Manager/Vendor. Mirrors ClientValidation.cs on the
/// Blazor client; that copy is a UX nicety only, this is what actually
/// gates the write. ReferralCode itself is already enforced as mandatory
/// inside CreateCrewHandler (distinct "no vendor found" vs "no vendor
/// selected" error messages live there, not here).
/// </summary>
public sealed class CreateCrewValidator : AbstractValidator<CreateCrewCommand>
{
    public CreateCrewValidator()
    {
        RuleFor(x => x.Mobile).NotEmpty().Matches(@"^\d{10}$")
            .WithMessage("Mobile number must be exactly 10 digits.");

        RuleFor(x => x.FullName).NotEmpty().MaximumLength(100)
            .Matches(@"^[A-Za-z ]+$")
            .WithMessage("Full name can only contain letters and spaces.");

        RuleFor(x => x.Email).NotEmpty().EmailAddress()
            .WithMessage("A valid email address is required.");
    }
}
