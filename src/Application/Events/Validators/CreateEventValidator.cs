using EventWOS.Application.Events.Commands;
using FluentValidation;

namespace EventWOS.Application.Events.Validators;

public sealed class CreateEventValidator : AbstractValidator<CreateEventCommand>
{
    public CreateEventValidator()
    {
        RuleFor(x => x.Title) .NotEmpty().MaximumLength(200);
        RuleFor(x => x.Venue) .NotEmpty().MaximumLength(300);
        RuleFor(x => x.StartAt).NotEmpty()
            .LessThan(x => x.EndAt).WithMessage("Start must be before End.");
        RuleFor(x => x.EndAt)  .NotEmpty();
        RuleFor(x => x.MaxCrew).GreaterThanOrEqualTo(0);
    }
}

public sealed class UpdateEventValidator : AbstractValidator<UpdateEventCommand>
{
    public UpdateEventValidator()
    {
        RuleFor(x => x.Title).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Venue).NotEmpty().MaximumLength(300);
        RuleFor(x => x.StartAt).NotEmpty()
            .LessThan(x => x.EndAt).WithMessage("Start must be before End.");
        RuleFor(x => x.EndAt).NotEmpty();
        RuleFor(x => x.MaxCrew).GreaterThanOrEqualTo(0);
    }
}
