using EventWOS.Application.Interfaces;
using EventWOS.Domain.Enums;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Notifications.Commands;

/// <summary>
/// Marks the caller's notifications read. One command covers a single row and
/// "mark all", because the only difference is the filter and two handlers would
/// be two places to forget the ownership check.
/// </summary>
/// <param name="NotificationIds">Null or empty means every unread notification.</param>
public sealed record MarkNotificationsReadCommand(
    Guid RecipientUserId,
    IReadOnlyCollection<Guid>? NotificationIds = null) : IRequest<Result<int>>;

public sealed class MarkNotificationsReadHandler
    : IRequestHandler<MarkNotificationsReadCommand, Result<int>>
{
    private readonly IAppDbContext _db;
    private readonly IUnitOfWork   _uow;

    public MarkNotificationsReadHandler(IAppDbContext db, IUnitOfWork uow)
    {
        _db  = db;
        _uow = uow;
    }

    public async Task<Result<int>> Handle(MarkNotificationsReadCommand req, CancellationToken ct)
    {
        // RecipientUserId is ALWAYS part of the filter, never merely checked after
        // loading. Without it in the query, a caller who guessed an id could mark
        // someone else's notifications read and quietly hide a cancellation from
        // them -- the ids arrive from the client, so they are not trustworthy.
        var query = _db.Notifications
            .Where(n => n.RecipientUserId == req.RecipientUserId && n.ReadAt == null);

        if (req.NotificationIds is { Count: > 0 } ids)
        {
            // Materialised so the same list drives the query on every provider,
            // and capped so "mark read" cannot be turned into an unbounded IN (...).
            var wanted = ids.Distinct().Take(500).ToList();
            query = query.Where(n => wanted.Contains(n.Id));
        }

        // Only unread rows are loaded, so a repeated call does no work and cannot
        // overwrite the original read time with a later one.
        var rows = await query.ToListAsync(ct);

        if (rows.Count == 0) return Result.Success(0);

        var now = DateTime.UtcNow;

        foreach (var notification in rows)
        {
            notification.MarkReadByRecipient(now);

            // The in-app delivery is advanced too, so the audit trail agrees with
            // what the user sees. Deliberately only InApp: reading it in the app
            // says nothing about whether the email or WhatsApp copy was opened,
            // and claiming otherwise would put a fact in the trail nobody observed.
            var inApp = notification.Deliveries
                .FirstOrDefault(d => d.Channel == NotificationChannel.InApp);

            inApp?.MarkRead(now);
        }

        await _uow.SaveChangesAsync(ct);

        return Result.Success(rows.Count);
    }
}
