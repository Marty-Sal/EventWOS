using EventOpsOracle.Application.Approval.DTOs;
using EventOpsOracle.Application.Approval.Queries;
using EventOpsOracle.Application.Files.DTOs;
using EventOpsOracle.Application.Interfaces;
using EventOpsOracle.Domain.Enums;
using EventOpsOracle.Domain.Interfaces;
using EventOpsOracle.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventOpsOracle.Application.Approval.Handlers;

/// <summary>
/// Returns Pending self-registrations the caller is allowed to act on.
///   Admin / Manager → Vendor registrations only (Crew approvals are
///                     delegated to the vendor who referred them).
///   Vendor          → Crew registrations whose ReferralCodeUsed maps
///                     to this vendor's referral code.
///   Anyone else     → empty.
/// The response keeps the (Vendors, Crew) shape so the same DTO works
/// for both UIs — only one of the two lists is populated for any
/// given caller.
/// </summary>
public sealed class GetApprovalQueueHandler : IRequestHandler<GetApprovalQueueQuery, Result<ApprovalQueueDto>>
{
    private readonly IAppDbContext  _db;
    private readonly ICurrentUser   _me;
    public GetApprovalQueueHandler(IAppDbContext db, ICurrentUser me)
    { _db = db; _me = me; }

    public async Task<Result<ApprovalQueueDto>> Handle(GetApprovalQueueQuery _, CancellationToken ct)
    {
        // ── Scope the query by caller role ────────────────────────────────
        IQueryable<Domain.Entities.User> q = _db.Users
            .Where(u => !u.IsDeleted && u.Status == UserStatus.Pending);

        if (_me.Role is UserRole.Admin or UserRole.Manager)
        {
            // Vendor accounts only — Crew approvals are the vendor's job.
            q = q.Where(u => u.Role == UserRole.Vendor);
        }
        else if (_me.Role == UserRole.Vendor)
        {
            // Crew whose ReferralCodeUsed matches MY referral code.
            // Stored uppercased on both sides so an exact compare is safe.
            var myRef = await _db.Users
                .Where(u => u.Id == _me.UserId)
                .Select(u => u.ReferralCode)
                .FirstOrDefaultAsync(ct);
            if (string.IsNullOrEmpty(myRef))
                return Result.Success(new ApprovalQueueDto(0, 0,
                    Array.Empty<PendingRegistrationDto>(),
                    Array.Empty<PendingRegistrationDto>()));
            q = q.Where(u => u.Role == UserRole.Crew && u.ReferralCodeUsed == myRef);
        }
        else
        {
            // Crew or anyone else has no queue.
            return Result.Success(new ApprovalQueueDto(0, 0,
                Array.Empty<PendingRegistrationDto>(),
                Array.Empty<PendingRegistrationDto>()));
        }

        var pending = await q
            .OrderBy(u => u.CreatedAt)
            .Select(u => new
            {
                u.Id, u.Username, u.Email, u.Mobile, u.FullName, u.Role, u.CreatedAt,
                u.BusinessName, u.ContactPersonName, u.City, u.Website,
                u.Skills, u.ExperienceYears, u.ReferralCodeUsed, u.VendorId,
                u.GstNumber, u.Address, u.State, u.Bio, u.DateOfBirth
            })
            .ToListAsync(ct);

        var vendorIds = pending.Where(p => p.VendorId.HasValue).Select(p => p.VendorId!.Value).Distinct().ToList();
        var vendorNames = vendorIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _db.Users.Where(u => vendorIds.Contains(u.Id))
                .Select(u => new { u.Id, Name = u.BusinessName ?? u.FullName })
                .ToDictionaryAsync(x => x.Id, x => x.Name, ct);

        // Docs uploaded during self-registration (profile photo / ID proof)
        // so the reviewer's "View details" modal can show/download them
        // without a separate round trip per row.
        var pendingIds = pending.Select(p => p.Id).ToList();
        var filesByOwner = pendingIds.Count == 0
            ? new Dictionary<Guid, List<FileDocumentDto>>()
            : (await _db.FileDocuments
                    .Where(f => pendingIds.Contains(f.OwnerId) && !f.IsDeleted)
                    .OrderBy(f => f.CreatedAt)
                    .Select(f => new
                    {
                        f.OwnerId, f.Id, f.EntityId, f.DocumentType, f.OriginalFileName,
                        f.ContentType, f.FileSizeBytes, f.ThumbnailStorageKey, f.CreatedAt
                    })
                    .ToListAsync(ct))
                .GroupBy(f => f.OwnerId)
                .ToDictionary(g => g.Key, g => g.Select(f => new FileDocumentDto(
                    f.Id, f.OwnerId, f.EntityId, f.DocumentType, f.OriginalFileName,
                    f.ContentType, f.FileSizeBytes, f.ThumbnailStorageKey is not null, f.CreatedAt)).ToList());

        var rows = pending.Select(p => new PendingRegistrationDto(
            p.Id, p.Username ?? "", p.Email ?? "", p.Mobile, p.FullName,
            p.Role.ToString(), p.CreatedAt,
            p.BusinessName, p.ContactPersonName, p.City, p.Website,
            p.Skills, p.ExperienceYears, p.ReferralCodeUsed,
            p.VendorId,
            p.VendorId.HasValue && vendorNames.TryGetValue(p.VendorId.Value, out var vn) ? vn : null,
            p.GstNumber, p.Address, p.State, p.Bio, p.DateOfBirth,
            filesByOwner.TryGetValue(p.Id, out var files)
                ? files
                : Array.Empty<FileDocumentDto>())).ToList();

        var vendors = rows.Where(r => r.Role == "Vendor").ToList();
        var crew    = rows.Where(r => r.Role == "Crew").ToList();
        return Result.Success(new ApprovalQueueDto(vendors.Count, crew.Count, vendors, crew));
    }
}
