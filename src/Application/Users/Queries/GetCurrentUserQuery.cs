using EventWOS.Application.Auth.Interfaces;
using EventWOS.Application.Users.DTOs;
using EventWOS.Application.Interfaces;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Users.Queries;

public sealed record GetCurrentUserQuery(Guid UserId) : IRequest<Result<UserProfileDto>>;

public sealed class GetCurrentUserHandler : IRequestHandler<GetCurrentUserQuery, Result<UserProfileDto>>
{
    private readonly IAppDbContext _db;
    private readonly IPermissionService _permissionService;

    public GetCurrentUserHandler(IAppDbContext db, IPermissionService permissionService)
    {
        _db = db;
        _permissionService = permissionService;
    }

    public async Task<Result<UserProfileDto>> Handle(GetCurrentUserQuery request, CancellationToken ct)
    {
        var user = await _db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == request.UserId && !u.IsDeleted, ct);

        if (user is null)
            return Result.Failure<UserProfileDto>(Error.UserNotFound);

        var permissions = await _permissionService.GetEffectivePermissionsAsync(user.Id, user.Role, ct);

        return Result.Success(new UserProfileDto(
            user.Id, user.Mobile, user.FullName, user.Email,
            user.AvatarUrl, user.Role, user.Status, permissions, user.LastLoginAt));
    }
}
