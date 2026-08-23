using EventWOS.Application.Auth.Interfaces;
using EventWOS.Application.Users.DTOs;
using EventWOS.Application.Events.Common;
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

        // "Total Events Done" / "Total Events Attended" are computed from the
        // caller's own assignment rows, not read from stored counter columns --
        // nothing ever wrote to those, so they reported 0 forever. The same
        // pass also gets the live/upcoming breakdown the dashboard tile shows
        // alongside the completed number.
        int? eventsCompleted = null, eventsLive = null, eventsUpcoming = null;
        if (user.Role == Domain.Enums.UserRole.Vendor)
        {
            var s = await VendorParticipationLoader.LoadSummaryAsync(_db, user.Id, ct);
            eventsCompleted = s.Completed;
            eventsLive      = s.Live;
            eventsUpcoming  = s.Upcoming;
        }
        else if (user.Role == Domain.Enums.UserRole.Crew)
        {
            var s = await CrewParticipationLoader.LoadSummaryAsync(_db, user.Id, ct);
            eventsCompleted = s.Completed;
            eventsLive      = s.Live;
            eventsUpcoming  = s.Upcoming;
        }

        // Load vendor name for crew members
        string? vendorName = null;
        if (user.Role == Domain.Enums.UserRole.Crew && user.VendorId.HasValue)
        {
            var vendor = await _db.Users.AsNoTracking()
                .Where(u => u.Id == user.VendorId.Value)
                .Select(u => new { u.FullName })
                .FirstOrDefaultAsync(ct);
            vendorName = vendor?.FullName;
        }

        return Result.Success(new UserProfileDto(
            user.Id, user.Username, user.Mobile, user.FullName, user.Email,
            user.AvatarUrl, user.Role, user.Status, permissions, user.LastLoginAt,
            // Vendor-specific
            user.ReferralCode,
            user.BusinessName,
            user.Role == Domain.Enums.UserRole.Vendor ? user.Rating : null,
            user.Role == Domain.Enums.UserRole.Vendor ? user.RatingCount : null,
            eventsCompleted,
            user.Role == Domain.Enums.UserRole.Vendor ? user.InviteMessageTemplate : null,
            // Crew-specific
            user.Role == Domain.Enums.UserRole.Crew ? user.DisciplineScore : null,
            user.Role == Domain.Enums.UserRole.Crew ? user.EventsAttended : null,
            user.Role == Domain.Enums.UserRole.Crew ? user.CrewRating : null,
            user.Role == Domain.Enums.UserRole.Crew ? user.CrewRatingCount : null,
            user.VendorId,
            vendorName,
            user.DateOfBirth,
            user.City,
            user.State,
            user.Address,
            user.Bio,
            user.Role == Domain.Enums.UserRole.Crew ? user.Skills : null,
            user.Role == Domain.Enums.UserRole.Crew ? user.ExperienceYears : null,
            user.Role == Domain.Enums.UserRole.Vendor ? user.ContactPersonName : null,
            user.Role == Domain.Enums.UserRole.Vendor ? user.GstNumber : null,
            user.Role == Domain.Enums.UserRole.Vendor ? user.Website : null,
            user.InvitedByUserId.HasValue,
            user.ProfileCompletedAt.HasValue,
            eventsLive,
            eventsUpcoming));
    }
}
