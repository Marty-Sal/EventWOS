using EventWOS.Application.Interfaces;
using EventWOS.Domain.Enums;
using EventWOS.Shared.Common;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.AuditLogs.Queries;

public sealed record AuditLogDto(
    Guid      Id,
    string    Action,
    string?   PerformedByUserId,
    string?   PerformedByName,    // resolved from Users — null if the actor is the system or a deleted user
    string?   PerformedByRole,    // resolved from Users
    string?   PerformedByIp,
    string    EntityType,
    string?   EntityId,
    string?   OldValues,
    string?   NewValues,
    string?   AdditionalData,
    DateTime  OccurredAt
);

public sealed record GetAuditLogsQuery(
    string?   EntityType = null,
    string?   EntityId   = null,
    Guid?     UserId     = null,
    string?   Action     = null,
    DateTime? From       = null,
    DateTime? To         = null,
    string?   ActorSearch = null,   // name substring (case-insensitive)
    int       PageNumber = 1,
    int       PageSize   = 50
) : IRequest<Result<PagedResult<AuditLogDto>>>;

public sealed class GetAuditLogsHandler
    : IRequestHandler<GetAuditLogsQuery, Result<PagedResult<AuditLogDto>>>
{
    private readonly IAppDbContext _db;
    public GetAuditLogsHandler(IAppDbContext db) => _db = db;

    public async Task<Result<PagedResult<AuditLogDto>>> Handle(
        GetAuditLogsQuery req, CancellationToken ct)
    {
        var query = _db.AuditLogs.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(req.EntityType))
            query = query.Where(a => a.EntityType == req.EntityType);
        if (!string.IsNullOrWhiteSpace(req.EntityId))
            query = query.Where(a => a.EntityId == req.EntityId);
        if (req.UserId.HasValue)
            query = query.Where(a => a.PerformedByUserId == req.UserId);
        if (!string.IsNullOrWhiteSpace(req.Action) &&
            Enum.TryParse<AuditAction>(req.Action, true, out var actionEnum))
            query = query.Where(a => a.Action == actionEnum);
        if (req.From.HasValue)
            query = query.Where(a => a.OccurredAt >= req.From.Value);
        if (req.To.HasValue)
            query = query.Where(a => a.OccurredAt <= req.To.Value);

        // LEFT JOIN to Users so we can show the actor's name + role.
        // System events (PerformedByUserId == null) and events from deleted users
        // pass through cleanly with null name/role.
        var joined =
            from a in query
            join u in _db.Users.AsNoTracking() on a.PerformedByUserId equals u.Id into gj
            from u in gj.DefaultIfEmpty()
            select new
            {
                a.Id, a.Action, a.PerformedByUserId, a.PerformedByIp,
                a.EntityType, a.EntityId, a.OldValues, a.NewValues,
                a.AdditionalData, a.OccurredAt,
                ActorName = u != null ? u.FullName : null,
                ActorRole = u != null ? u.Role.ToString() : null
            };

        if (!string.IsNullOrWhiteSpace(req.ActorSearch))
        {
            var needle = req.ActorSearch.Trim().ToLower();
            joined = joined.Where(x => x.ActorName != null && x.ActorName.ToLower().Contains(needle));
        }

        var total = await joined.CountAsync(ct);
        var items = await joined
            .OrderByDescending(x => x.OccurredAt)
            .Skip((req.PageNumber - 1) * req.PageSize)
            .Take(req.PageSize)
            .Select(x => new AuditLogDto(
                x.Id, x.Action.ToString(),
                x.PerformedByUserId.HasValue ? x.PerformedByUserId.Value.ToString() : null,
                x.ActorName, x.ActorRole,
                x.PerformedByIp, x.EntityType, x.EntityId,
                x.OldValues, x.NewValues, x.AdditionalData, x.OccurredAt))
            .ToListAsync(ct);

        return Result.Success(PagedResult<AuditLogDto>.Create(items, total, req.PageNumber, req.PageSize));
    }
}
