using EventWOS.Application.Interfaces;
using EventWOS.Domain.Entities;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Announcements.Commands;

/// <summary>
/// Records that the caller has read an announcement. Idempotent — a second
/// call is a no-op rather than a duplicate-key error (the unique index on
/// (announcement_id, user_id) is the backstop).
/// </summary>
public sealed record MarkAnnouncementReadCommand(Guid AnnouncementId, Guid UserId)
    : IRequest<Result>;

public sealed class MarkAnnouncementReadHandler : IRequestHandler<MarkAnnouncementReadCommand, Result>
{
    private readonly IAppDbContext _db;
    public MarkAnnouncementReadHandler(IAppDbContext db) => _db = db;

    public async Task<Result> Handle(MarkAnnouncementReadCommand req, CancellationToken ct)
    {
        var exists = await _db.EventAnnouncements
            .AnyAsync(a => a.Id == req.AnnouncementId && !a.IsDeleted, ct);
        if (!exists) return Result.Failure(Error.NotFound);

        var already = await _db.EventAnnouncementReads
            .AnyAsync(r => r.AnnouncementId == req.AnnouncementId && r.UserId == req.UserId && !r.IsDeleted, ct);
        if (already) return Result.Success();

        _db.EventAnnouncementReads.Add(new EventAnnouncementRead(req.AnnouncementId, req.UserId));
        await _db.SaveChangesAsync(ct);
        return Result.Success();
    }
}
