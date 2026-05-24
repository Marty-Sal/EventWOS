using EventWOS.Application.Interfaces;
using EventWOS.Domain.Enums;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Vendors.Commands;

public sealed record RateVendorCommand(Guid VendorId, decimal Rating) : IRequest<Result>;

public sealed class RateVendorHandler : IRequestHandler<RateVendorCommand, Result>
{
    private readonly IAppDbContext _db;
    public RateVendorHandler(IAppDbContext db) => _db = db;

    public async Task<Result> Handle(RateVendorCommand req, CancellationToken ct)
    {
        var vendor = await _db.Users.FirstOrDefaultAsync(
            u => u.Id == req.VendorId && u.Role == UserRole.Vendor && !u.IsDeleted, ct);
        if (vendor is null) return Result.Failure(new Error("Vendor.NotFound", "Vendor not found."));

        vendor.SetRating(req.Rating);
        await _db.SaveChangesAsync(ct);
        return Result.Success();
    }
}
