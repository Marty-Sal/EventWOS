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
        var query = _db.AuditLogs.AsQueryable();

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

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(a => a.OccurredAt)
            .Skip((req.PageNumber - 1) * req.PageSize)
            .Take(req.PageSize)
            .Select(a => new AuditLogDto(
                a.Id, a.Action.ToString(),
                a.PerformedByUserId.HasValue ? a.PerformedByUserId.Value.ToString() : null,
                a.PerformedByIp, a.EntityType, a.EntityId,
                a.OldValues, a.NewValues, a.AdditionalData, a.OccurredAt))
            .ToListAsync(ct);

        return Result.Success(PagedResult<AuditLogDto>.Create(items, total, req.PageNumber, req.PageSize));
    }
}
