using EventWOS.Application.Approval.DTOs;
using EventWOS.Application.Approval.Queries;
using EventWOS.Application.Interfaces;
using EventWOS.Domain.Enums;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Approval.Handlers;

/// <summary>
/// Returns all Pending users grouped by role, oldest first. Joins to
/// the referred Vendor on the Crew side so the UI can display
/// "Referred by: Acme Events" rather than just a code.
/// </summary>
public sealed class GetApprovalQueueHandler : IRequestHandler<GetApprovalQueueQuery, Result<ApprovalQueueDto>>
{
    private readonly IAppDbContext _db;
    public GetApprovalQueueHandler(IAppDbContext db) => _db = db;

    public async Task<Result<ApprovalQueueDto>> Handle(GetApprovalQueueQuery _, CancellationToken ct)
    {
        var pending = await _db.Users
            .Where(u => !u.IsDeleted && u.Status == UserStatus.Pending
                     && (u.Role == UserRole.Vendor || u.Role == UserRole.Crew))
            .OrderBy(u => u.CreatedAt)
            .Select(u => new
            {
                u.Id, u.Username, u.Email, u.Mobile, u.FullName, u.Role, u.CreatedAt,
                u.BusinessName, u.ContactPersonName, u.City, u.Website,
                u.Skills, u.ExperienceYears, u.ReferralCodeUsed, u.VendorId
            })
            .ToListAsync(ct);

        // Single roundtrip for vendor names — only fetch what we need.
        var vendorIds = pending.Where(p => p.VendorId.HasValue).Select(p => p.VendorId!.Value).Distinct().ToList();
        var vendorNames = vendorIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _db.Users.Where(u => vendorIds.Contains(u.Id))
                .Select(u => new { u.Id, Name = u.BusinessName ?? u.FullName })
                .ToDictionaryAsync(x => x.Id, x => x.Name, ct);

        var rows = pending.Select(p => new PendingRegistrationDto(
            p.Id, p.Username ?? "", p.Email ?? "", p.Mobile, p.FullName,
            p.Role.ToString(), p.CreatedAt,
            p.BusinessName, p.ContactPersonName, p.City, p.Website,
            p.Skills, p.ExperienceYears, p.ReferralCodeUsed,
            p.VendorId,
            p.VendorId.HasValue && vendorNames.TryGetValue(p.VendorId.Value, out var vn) ? vn : null)).ToList();

        var vendors = rows.Where(r => r.Role == "Vendor").ToList();
        var crew    = rows.Where(r => r.Role == "Crew").ToList();

        return Result.Success(new ApprovalQueueDto(vendors.Count, crew.Count, vendors, crew));
    }
}
