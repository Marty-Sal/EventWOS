using EventWOS.Domain.Enums;
using EventWOS.Domain.Interfaces;
using EventWOS.Application.Interfaces;
using EventWOS.Shared.Result;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Users.Commands;

public sealed record UpdateProfileCommand(
    Guid UserId,
    string FullName,
    string? Email,
    string? AvatarUrl
) : IRequest<Result>;

public sealed class UpdateProfileValidator : AbstractValidator<UpdateProfileCommand>
{
    public UpdateProfileValidator()
    {
        RuleFor(x => x.FullName)
            .NotEmpty().WithMessage("Full name is required.")
            .MaximumLength(100).WithMessage("Full name cannot exceed 100 characters.");

        RuleFor(x => x.Email)
            .EmailAddress().WithMessage("Email must be a valid RFC email address.")
            .When(x => !string.IsNullOrWhiteSpace(x.Email));
    }
}

public sealed class UpdateProfileHandler : IRequestHandler<UpdateProfileCommand, Result>
{
    private readonly IAppDbContext _db;
    private readonly IUnitOfWork _uow;
    private readonly IAuditLogger _audit;

    public UpdateProfileHandler(IAppDbContext db, IUnitOfWork uow, IAuditLogger audit)
    {
        _db = db; _uow = uow; _audit = audit;
    }

    public async Task<Result> Handle(UpdateProfileCommand request, CancellationToken ct)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == request.UserId && !u.IsDeleted, ct);
        if (user is null) return Result.Failure(Error.UserNotFound);

        var oldSnapshot = new { user.FullName, user.Email, user.AvatarUrl };
        user.UpdateProfile(request.FullName, request.Email, request.AvatarUrl);

        await _uow.SaveChangesAsync(ct);

        await _audit.LogAsync(AuditAction.UserUpdated, "User", user.Id.ToString(),
            oldValues: oldSnapshot,
            newValues: new { request.FullName, request.Email, request.AvatarUrl },
            cancellationToken: ct);

        return Result.Success();
    }
}
