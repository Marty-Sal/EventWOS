using EventWOS.Application.Interfaces;
using EventWOS.Domain.Enums;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Ratings.Queries;

/// <summary>One vendor on an event, and whether they have been rated for it yet.</summary>
public sealed record EventVendorToRateDto(
    Guid     VendorUserId,
    string   VendorName,
    string?  BusinessName,
    bool     AlreadyRated,
    int?     Performance,
    int?     Cooperation,
    string?  Comment);

/// <summary>
/// The vendors who worked an event, for the "rate your vendors" prompt shown when
/// the event is marked Completed.
///
/// Already-rated vendors are returned too, flagged rather than filtered. A prompt
/// that silently drops them would leave the rater unsure whether someone was
/// missed or already done, and revising a rating is allowed.
/// </summary>
public sealed record GetEventVendorsToRateQuery(Guid EventId)
    : IRequest<Result<IReadOnlyList<EventVendorToRateDto>>>;

public sealed class GetEventVendorsToRateHandler
    : IRequestHandler<GetEventVendorsToRateQuery, Result<IReadOnlyList<EventVendorToRateDto>>>
{
    private readonly IAppDbContext _db;
    public GetEventVendorsToRateHandler(IAppDbContext db) => _db = db;

    public async Task<Result<IReadOnlyList<EventVendorToRateDto>>> Handle(
        GetEventVendorsToRateQuery req, CancellationToken ct)
    {
        // Both routes onto an event count, exactly as the write path checks: a
        // vendor holding only a shift quota still worked the event.
        var viaAllocation = _db.VendorShiftAllocations
            .Where(a => a.Shift!.EventId == req.EventId && !a.IsDeleted)
            .Select(a => a.VendorId);

        var viaAssignment = _db.EventAssignments
            .Where(a => a.EventId == req.EventId && a.VendorId != null && !a.IsDeleted)
            .Select(a => a.VendorId!.Value);

        var vendorIds = await viaAllocation.Concat(viaAssignment).Distinct().ToListAsync(ct);
        if (vendorIds.Count == 0)
            return Result.Success<IReadOnlyList<EventVendorToRateDto>>(
                Array.Empty<EventVendorToRateDto>());

        var vendors = await _db.Users
            .Where(u => vendorIds.Contains(u.Id) && !u.IsDeleted)
            .OrderBy(u => u.FullName)
            .Select(u => new { u.Id, u.FullName, u.BusinessName })
            .ToListAsync(ct);

        var existing = await _db.Ratings
            .Where(r => r.EventId == req.EventId
                     && r.SubjectType == RatingSubjectType.Vendor
                     && vendorIds.Contains(r.SubjectUserId))
            .Select(r => new { r.SubjectUserId, r.Performance, r.Cooperation, r.Comment })
            .ToListAsync(ct);

        var byVendor = existing.ToDictionary(r => r.SubjectUserId);

        var items = vendors.Select(v =>
        {
            var hit = byVendor.GetValueOrDefault(v.Id);
            return new EventVendorToRateDto(
                v.Id, v.FullName, v.BusinessName,
                AlreadyRated: hit is not null,
                Performance:  hit?.Performance,
                Cooperation:  hit?.Cooperation,
                Comment:      hit?.Comment);
        }).ToList();

        return Result.Success<IReadOnlyList<EventVendorToRateDto>>(items);
    }
}
