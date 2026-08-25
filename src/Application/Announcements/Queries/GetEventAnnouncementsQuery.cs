using EventOpsOracle.Application.Announcements.DTOs;
using EventOpsOracle.Application.Interfaces;
using EventOpsOracle.Domain.Enums;
using EventOpsOracle.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventOpsOracle.Application.Announcements.Queries;

/// <summary>
/// Full notification history for one event, newest first — rendered both on
/// the Admin/Manager event screen and to an assigned vendor/crew member.
///
/// Non-privileged callers must be connected to the event AND the announcement's
/// audience must include their role (a crew member never sees a vendors-only
/// broadcast).
/// </summary>
public sealed record GetEventAnnouncementsQuery(
    Guid EventId,
    Guid RequestingUserId,
    UserRole RequestingUserRole,
    bool IsPrivileged
) : IRequest<Result<IReadOnlyList<EventAnnouncementDto>>>;

public sealed class GetEventAnnouncementsHandler
    : IRequestHandler<GetEventAnnouncementsQuery, Result<IReadOnlyList<EventAnnouncementDto>>>
{
    private readonly IAppDbContext _db;
    public GetEventAnnouncementsHandler(IAppDbContext db) => _db = db;

    public async Task<Result<IReadOnlyList<EventAnnouncementDto>>> Handle(
        GetEventAnnouncementsQuery req, CancellationToken ct)
    {
        var ev = await _db.Events.FirstOrDefaultAsync(e => e.Id == req.EventId && !e.IsDeleted, ct);
        if (ev is null)
            return Result.Failure<IReadOnlyList<EventAnnouncementDto>>(Error.NotFound);

        if (!req.IsPrivileged)
        {
            var connected = await AnnouncementAccess.IsConnectedToEventAsync(
                _db, req.EventId, req.RequestingUserId, req.RequestingUserRole, ct);
            if (!connected)
                return Result.Failure<IReadOnlyList<EventAnnouncementDto>>(Error.Unauthorized);
        }

        var announcements = await _db.EventAnnouncements
            .Where(a => a.EventId == req.EventId && !a.IsDeleted)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(ct);

        if (!req.IsPrivileged)
        {
            announcements = announcements
                .Where(a => AnnouncementAccess.Includes(a.Audience, req.RequestingUserRole))
                .ToList();
        }

        if (announcements.Count == 0)
            return Result.Success<IReadOnlyList<EventAnnouncementDto>>(Array.Empty<EventAnnouncementDto>());

        var dtos = await AnnouncementDtoBuilder.BuildAsync(
            _db, announcements, ev.Title, ev.StartAt, req.RequestingUserId, ct);

        return Result.Success(dtos);
    }
}
