using EventWOS.Application.Files.DTOs;
using EventWOS.Application.Interfaces;
using EventWOS.Application.Vendors.DTOs;
using EventWOS.Domain.Enums;
using EventWOS.Domain.Interfaces;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Crew.Queries;

/// <summary>
/// Full profile for the Crew page's "View details" modal — mirrors the
/// depth the Approval Queue shows for a pending Crew registration
/// (city, DOB, skills, bio, uploaded documents), but works for a Crew
/// member in ANY status, not just Pending.
///
/// Authorization is role-scoped here (not just [Permission("crew:read")]
/// at the controller) because crew:read is broad — Admin/Manager can look
/// up anyone, but a Vendor must only be able to open details for crew
/// that actually belongs to them, same restriction GetCrewQuery already
/// applies to the list endpoint.
/// </summary>
public sealed record GetCrewByIdQuery(Guid CrewId) : IRequest<Result<CrewDetailDto>>;

public sealed class GetCrewByIdHandler : IRequestHandler<GetCrewByIdQuery, Result<CrewDetailDto>>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser  _me;
    public GetCrewByIdHandler(IAppDbContext db, ICurrentUser me) { _db = db; _me = me; }

    public async Task<Result<CrewDetailDto>> Handle(GetCrewByIdQuery req, CancellationToken ct)
    {
        var crew = await _db.Users.FirstOrDefaultAsync(
            u => u.Id == req.CrewId && u.Role == UserRole.Crew && !u.IsDeleted, ct);
        if (crew is null) return Result.Failure<CrewDetailDto>(new Error("Crew.NotFound", "Crew member not found."));

        if (_me.Role == UserRole.Vendor && crew.VendorId != _me.UserId)
            return Result.Failure<CrewDetailDto>(new Error("Crew.NotFound", "Crew member not found."));

        string? vendorName = null;
        if (crew.VendorId.HasValue)
        {
            vendorName = await _db.Users
                .Where(u => u.Id == crew.VendorId)
                .Select(u => u.BusinessName ?? u.FullName)
                .FirstOrDefaultAsync(ct);
        }

        var files = await _db.FileDocuments
            .Where(f => f.OwnerId == req.CrewId && !f.IsDeleted)
            .OrderBy(f => f.CreatedAt)
            .Select(f => new FileDocumentDto(
                f.Id, f.OwnerId, f.EntityId, f.DocumentType, f.OriginalFileName,
                f.ContentType, f.FileSizeBytes, f.ThumbnailStorageKey != null, f.CreatedAt))
            .ToListAsync(ct);

        return Result.Success(new CrewDetailDto(
            crew.Id, crew.Mobile, crew.FullName, crew.Email, crew.AvatarUrl,
            crew.Status.ToString(), crew.VendorId, vendorName,
            crew.DisciplineScore, crew.EventsAttended, crew.CreatedAt,
            crew.City, crew.State, crew.Bio, crew.Skills, crew.ExperienceYears,
            crew.ReferralCodeUsed, crew.DateOfBirth, files,
            crew.InvitedByUserId.HasValue, crew.ProfileCompletedAt.HasValue));
    }
}
