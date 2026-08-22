using EventWOS.Application.Announcements.DTOs;
using EventWOS.Application.Interfaces;
using EventWOS.Domain.Enums;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Announcements.Queries;

/// <summary>
/// A vendor's / crew member's own notification inbox — every announcement for
/// every event they're connected to, newest first. Powers the dashboard panel
/// and its unread badge.
///
/// Computed live from current assignments rather than from a recipient
/// snapshot, so someone added to an event later still sees the notifications
/// that went out before they joined.
/// </summary>
public sealed record GetMyAnnouncementsQuery(
    Guid UserId,
    UserRole Role,
    int Take = 50
) : IRequest<Result<IReadOnlyList<EventAnnouncementDto>>>;

public sealed class GetMyAnnouncementsHandler
    : IRequestHandler<GetMyAnnouncementsQuery, Result<IReadOnlyList<EventAnnouncementDto>>>
{
    private readonly IAppDbContext _db;
    public GetMyAnnouncementsHandler(IAppDbContext db) => _db = db;

    public async Task<Result<IReadOnlyList<EventAnnouncementDto>>> Handle(
        GetMyAnnouncementsQuery req, CancellationToken ct)
    {
        var eventIds = await ResolveMyEventIdsAsync(req.UserId, req.Role, ct);
        if (eventIds.Count == 0)
            return Result.Success<IReadOnlyList<EventAnnouncementDto>>(Array.Empty<EventAnnouncementDto>());

        var announcements = await _db.EventAnnouncements
            .Where(a => eventIds.Contains(a.EventId) && !a.IsDeleted)
            .OrderByDescending(a => a.CreatedAt)
            .Take(Math.Clamp(req.Take, 1, 200))
            .ToListAsync(ct);

        announcements = announcements
            .Where(a => AnnouncementAccess.Includes(a.Audience, req.Role))
            .ToList();

        if (announcements.Count == 0)
            return Result.Success<IReadOnlyList<EventAnnouncementDto>>(Array.Empty<EventAnnouncementDto>());

        var involvedEventIds = announcements.Select(a => a.EventId).Distinct().ToList();
        var events = await _db.Events
            .Where(e => involvedEventIds.Contains(e.Id))
            .Select(e => new { e.Id, e.Title, e.StartAt })
            .ToListAsync(ct);
        var eventsById = events.ToDictionary(e => e.Id, e => (e.Title, e.StartAt));

        var dtos = await AnnouncementDtoBuilder.BuildAsync(
            _db, announcements,
            id => eventsById.TryGetValue(id, out var info) ? info : ("Event", DateTime.MinValue),
            req.UserId, ct);

        return Result.Success(dtos);
    }

    /// <summary>Events the user is connected to — invites for crew, invites + shift quotas for vendors.</summary>
    private async Task<List<Guid>> ResolveMyEventIdsAsync(Guid userId, UserRole role, CancellationToken ct)
    {
        var ids = new HashSet<Guid>();

        if (role == UserRole.Crew)
        {
            var fromAssignments = await _db.EventAssignments
                .Where(a => a.CrewId == userId && !a.IsDeleted)
                .Select(a => a.EventId)
                .Distinct()
                .ToListAsync(ct);
            foreach (var id in fromAssignments) ids.Add(id);
        }
        else if (role == UserRole.Vendor)
        {
            var fromAssignments = await _db.EventAssignments
                .Where(a => a.VendorId == userId && !a.IsDeleted)
                .Select(a => a.EventId)
                .Distinct()
                .ToListAsync(ct);
            foreach (var id in fromAssignments) ids.Add(id);

            var fromQuotas = await _db.VendorShiftAllocations
                .Where(q => q.VendorId == userId && !q.IsDeleted)
                .Join(_db.EventShifts.Where(s => !s.IsDeleted), q => q.ShiftId, s => s.Id, (q, s) => s.EventId)
                .Distinct()
                .ToListAsync(ct);
            foreach (var id in fromQuotas) ids.Add(id);
        }

        return ids.ToList();
    }
}
