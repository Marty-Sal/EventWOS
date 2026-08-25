using EventOpsOracle.Application.Users.DTOs;
using EventOpsOracle.Domain.Enums;
using EventOpsOracle.Application.Interfaces;
using EventOpsOracle.Shared.Common;
using EventOpsOracle.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventOpsOracle.Application.Users.Queries;

public sealed record GetUsersQuery(
    int PageNumber = 1,
    int PageSize = 20,
    string? Search = null,
    UserRole? Role = null,
    UserStatus? Status = null
) : IRequest<Result<PagedResult<UserDto>>>;

public sealed class GetUsersHandler : IRequestHandler<GetUsersQuery, Result<PagedResult<UserDto>>>
{
    private readonly IAppDbContext _db;

    public GetUsersHandler(IAppDbContext db) => _db = db;

    public async Task<Result<PagedResult<UserDto>>> Handle(GetUsersQuery request, CancellationToken ct)
    {
        var query = _db.Users.AsNoTracking().Where(u => !u.IsDeleted);

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var searchLower = request.Search.Trim().ToLower();
            query = query.Where(u =>
                u.Mobile.ToLower().Contains(searchLower) ||
                u.FullName.ToLower().Contains(searchLower) ||
                (u.Email != null && u.Email.ToLower().Contains(searchLower)));
        }

        if (request.Role.HasValue)
            query = query.Where(u => u.Role == request.Role.Value);

        if (request.Status.HasValue)
            query = query.Where(u => u.Status == request.Status.Value);

        var total = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(u => u.CreatedAt)
            .Skip((request.PageNumber - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(u => new UserDto(
                u.Id, u.Mobile, u.FullName, u.Email,
                u.AvatarUrl, u.Role, u.Status, u.ManagerId, u.LastLoginAt, u.CreatedAt,
                // Cached aggregates, so listing users costs no extra query per row.
                u.Rating, u.RatingCount, u.CrewRating, u.CrewRatingCount))
            .ToListAsync(ct);

        return Result.Success(PagedResult<UserDto>.Create(items, total, request.PageNumber, request.PageSize));
    }
}
