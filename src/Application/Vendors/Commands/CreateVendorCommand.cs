using EventWOS.Application.Interfaces;
using EventWOS.Application.Vendors.DTOs;
using EventWOS.Domain.Entities;
using EventWOS.Domain.Enums;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Vendors.Commands;

public sealed record CreateVendorCommand(
    string Mobile, string FullName, string? BusinessName, string? Email
) : IRequest<Result<VendorDto>>;

public sealed class CreateVendorHandler : IRequestHandler<CreateVendorCommand, Result<VendorDto>>
{
    private readonly IAppDbContext _db;

    public CreateVendorHandler(IAppDbContext db) => _db = db;

    public async Task<Result<VendorDto>> Handle(CreateVendorCommand req, CancellationToken ct)
    {
        if (await _db.Users.AnyAsync(u => u.Mobile == req.Mobile, ct))
            return Result.Failure<VendorDto>(new Error("Vendor.DuplicateMobile", "Mobile already registered."));

        var vendor = new User(req.Mobile, req.FullName, UserRole.Vendor);
        vendor.Activate();
        if (req.BusinessName is not null) vendor.BusinessName = req.BusinessName;
        if (req.Email is not null)        vendor.Email        = req.Email;

        _db.Users.Add(vendor);
        await _db.SaveChangesAsync(ct);

        return Result.Success(MapToDto(vendor, 0));
    }

    internal static VendorDto MapToDto(User v, int crewCount) => new(
        v.Id, v.Mobile, v.FullName, v.BusinessName, v.Email, v.AvatarUrl,
        v.Status.ToString(), v.ReferralCode, v.Rating, v.EventsCompleted, crewCount, v.CreatedAt);
}
