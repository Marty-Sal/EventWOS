using EventWOS.Application.Interfaces;
using EventWOS.Application.Notifications.Rendering;
using EventWOS.Domain.Enums;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace EventWOS.Application.Notifications.Queries;

/// <summary>One row in the recipient's notification inbox.</summary>
public sealed record MyNotificationDto(
    Guid     Id,
    string   Code,
    string?  Title,
    string   Body,
    string   Priority,
    Guid?    EventId,
    bool     IsRead,
    DateTime CreatedAt);

public sealed record MyNotificationsDto(
    IReadOnlyList<MyNotificationDto> Items,
    int UnreadCount,
    int Total);

/// <summary>
/// The recipient's own inbox. This is what makes the platform's notifications
/// survivable: a toast lasts five seconds and only reaches someone who was
/// connected at the time, so without this a crew member offline when their event
/// was cancelled had nothing to come back to.
/// </summary>
/// <param name="UnreadOnly">Used by the bell dropdown; the full page shows everything.</param>
public sealed record GetMyNotificationsQuery(
    Guid RecipientUserId,
    bool UnreadOnly = false,
    int  Skip = 0,
    int  Take = 30) : IRequest<Result<MyNotificationsDto>>;

public sealed class GetMyNotificationsHandler
    : IRequestHandler<GetMyNotificationsQuery, Result<MyNotificationsDto>>
{
    private readonly IAppDbContext _db;
    private readonly INotificationTemplateRenderer _renderer;

    public GetMyNotificationsHandler(IAppDbContext db, INotificationTemplateRenderer renderer)
    {
        _db       = db;
        _renderer = renderer;
    }

    public async Task<Result<MyNotificationsDto>> Handle(
        GetMyNotificationsQuery req, CancellationToken ct)
    {
        // Capped rather than trusted: a caller asking for 100000 would otherwise
        // pull an entire history into memory to render it.
        var take = Math.Clamp(req.Take, 1, 100);
        var skip = Math.Max(req.Skip, 0);

        // Only notifications that actually have an in-app delivery belong here.
        // A WhatsApp-only notification was never meant to appear in the app, and
        // showing it would imply the app is a complete record when it is not.
        var mine = _db.Notifications
            .AsNoTracking()
            .Where(n => n.RecipientUserId == req.RecipientUserId
                     && n.Deliveries.Any(d => d.Channel == NotificationChannel.InApp));

        // Unread is the parent's ReadAt: the recipient reads the notification, not
        // one channel's copy of it. Marking it read in the app should not depend on
        // which channel happened to arrive first.
        if (req.UnreadOnly)
            mine = mine.Where(n => n.ReadAt == null);

        var total       = await mine.CountAsync(ct);

        // Always the true unread total, never a count of the current page --
        // a badge that says 30 when 214 are waiting is worse than no badge.
        var unreadCount = await _db.Notifications
            .AsNoTracking()
            .CountAsync(n => n.RecipientUserId == req.RecipientUserId
                          && n.ReadAt == null
                          && n.Deliveries.Any(d => d.Channel == NotificationChannel.InApp), ct);

        var rows = await mine
            .OrderByDescending(n => n.CreatedAt)
            .Skip(skip)
            .Take(take)
            .Select(n => new
            {
                n.Id,
                n.TemplateCode,
                n.Priority,
                n.EventId,
                n.ReadAt,
                n.CreatedAt,
                n.DataJson,
                // The version that was current when the notification was created,
                // so an old message renders with the wording it was sent with.
                TemplateVersion = n.Deliveries
                    .Where(d => d.Channel == NotificationChannel.InApp)
                    .Select(d => (int?)d.TemplateVersion)
                    .FirstOrDefault()
            })
            .ToListAsync(ct);

        if (rows.Count == 0)
            return Result.Success(new MyNotificationsDto(Array.Empty<MyNotificationDto>(), unreadCount, total));

        // Templates are fetched once for the whole page, not per row: an inbox of
        // 30 assignment notifications would otherwise issue 30 identical queries.
        var codes = rows.Select(r => r.TemplateCode).Distinct().ToList();

        var templates = await _db.NotificationTemplates
            .AsNoTracking()
            .Where(t => codes.Contains(t.Code) && t.Channel == NotificationChannel.InApp && t.IsActive)
            .ToListAsync(ct);

        var items = new List<MyNotificationDto>(rows.Count);

        foreach (var row in rows)
        {
            // Prefer the exact version the notification was sent with; fall back to
            // the newest active template, because a template that was retired since
            // must not blank out an existing message in someone's inbox.
            var template =
                templates.FirstOrDefault(t => t.Code == row.TemplateCode && t.Version == row.TemplateVersion)
                ?? templates.Where(t => t.Code == row.TemplateCode)
                            .OrderByDescending(t => t.Version)
                            .FirstOrDefault();

            string? title;
            string  body;

            if (template is null)
            {
                // No template at all: show the code rather than dropping the row.
                // Something happened to this person and silence is the worst answer.
                title = row.TemplateCode.Replace('_', ' ');
                body  = "This notification is no longer available in full.";
            }
            else
            {
                var rendered = _renderer.Render(template, Deserialize(row.DataJson));
                title = rendered.Subject;
                body  = rendered.Body;
            }

            items.Add(new MyNotificationDto(
                row.Id,
                row.TemplateCode,
                title,
                body,
                row.Priority.ToString(),
                row.EventId,
                IsRead: row.ReadAt is not null,
                row.CreatedAt));
        }

        return Result.Success(new MyNotificationsDto(items, unreadCount, total));
    }

    /// <summary>
    /// Placeholder data is stored as jsonb. Corrupt or unexpected json must not
    /// take down an entire inbox page, so it degrades to no tokens: the reader
    /// sees the template's skeleton instead of an error screen.
    /// </summary>
    private static IReadOnlyDictionary<string, string?> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, string?>();

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string?>>(json)
                   ?? new Dictionary<string, string?>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string?>();
        }
    }
}
