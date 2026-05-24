using EventWOS.Application.Interfaces;
using EventWOS.Domain.Enums;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Vendors.Commands;

public sealed record ChangeVendorStatusCommand(Guid VendorId, string Status, Guid ActorId) : IRequest<Result>;

public sealed class ChangeVendorStatusHandler : IRequestHandler<ChangeVendorStatusCommand, Result>
{
    private readonly IAppDbContext _db;
    public ChangeVendorStatusHandler(IAppDbContext db) => _db = db;

    public async Task<Result> Handle(ChangeVendorStatusCommand req, CancellationToken ct)
    {
        var vendor = await _db.Users.FirstOrDefaultAsync(
            u => u.Id == req.VendorId && u.Role == UserRole.Vendor && !u.IsDeleted, ct);
        if (vendor is null) return Result.Failure(new Error("Vendor.NotFound", "Vendor not found."));

        switch (req.Status.ToLower())
        {
            case "active":      vendor.Reactivate(req.ActorId); break;
            case "suspended":   vendor.Suspend(req.ActorId);    break;
            case "deactivated": vendor.Deactivate(req.ActorId); break;
            default: return Result.Failure(new Error("Vendor.InvalidStatus", "Invalid status value."));
        }

        await _db.SaveChangesAsync(ct);
        return Result.Success();
    }
}
