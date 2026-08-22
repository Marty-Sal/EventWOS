using EventWOS.Application.Files.DTOs;
using EventWOS.Application.Events.Common;
using EventWOS.Application.Interfaces;
using EventWOS.Application.Vendors.Commands;
using EventWOS.Application.Vendors.DTOs;
using EventWOS.Domain.Enums;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Vendors.Queries;

public sealed record GetVendorByIdQuery(Guid VendorId) : IRequest<Result<VendorDto>>;

public sealed class GetVendorByIdHandler : IRequestHandler<GetVendorByIdQuery, Result<VendorDto>>
{
    private readonly IAppDbContext _db;
    public GetVendorByIdHandler(IAppDbContext db) => _db = db;

    public async Task<Result<VendorDto>> Handle(GetVendorByIdQuery req, CancellationToken ct)
    {
        var vendor = await _db.Users.FirstOrDefaultAsync(
            u => u.Id == req.VendorId && u.Role == UserRole.Vendor && !u.IsDeleted, ct);
        if (vendor is null) return Result.Failure<VendorDto>(new Error("Vendor.NotFound", "Vendor not found."));

        var crewCount = await _db.Users.CountAsync(
            u => u.VendorId == req.VendorId && u.Role == UserRole.Crew && !u.IsDeleted, ct);

        // Profile photo / any docs the vendor uploaded — same "View details"
        // depth an Admin/Manager gets in the Approval Queue for a still-
        // pending Vendor registration.
        var files = await _db.FileDocuments
            .Where(f => f.OwnerId == req.VendorId && !f.IsDeleted)
            .OrderBy(f => f.CreatedAt)
            .Select(f => new FileDocumentDto(
                f.Id, f.OwnerId, f.EntityId, f.DocumentType, f.OriginalFileName,
                f.ContentType, f.FileSizeBytes, f.ThumbnailStorageKey != null, f.CreatedAt))
            .ToListAsync(ct);

        var eventsDone = await VendorParticipationLoader.CountEventsDoneAsync(_db, vendor.Id, ct);

        return Result.Success(CreateVendorHandler.MapToDto(vendor, crewCount, files, eventsDone));
    }
}
