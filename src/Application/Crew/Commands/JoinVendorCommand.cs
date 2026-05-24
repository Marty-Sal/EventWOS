using EventWOS.Application.Interfaces;
using EventWOS.Domain.Enums;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Crew.Commands;

public sealed record JoinVendorCommand(Guid CrewId, string ReferralCode) : IRequest<Result>;

public sealed class JoinVendorHandler : IRequestHandler<JoinVendorCommand, Result>
{
    private readonly IAppDbContext _db;
    public JoinVendorHandler(IAppDbContext db) => _db = db;

    public async Task<Result> Handle(JoinVendorCommand req, CancellationToken ct)
    {
        var crew = await _db.Users.FirstOrDefaultAsync(
            u => u.Id == req.CrewId && u.Role == UserRole.Crew && !u.IsDeleted, ct);
        if (crew is null) return Result.Failure(new Error("Crew.NotFound", "Crew member not found."));

        var vendor = await _db.Users.FirstOrDefaultAsync(
            u => u.ReferralCode == req.ReferralCode && u.Role == UserRole.Vendor && !u.IsDeleted, ct);
        if (vendor is null) return Result.Failure(new Error("Crew.InvalidReferral", "Invalid referral code."));

        crew.JoinVendor(vendor.Id);
        await _db.SaveChangesAsync(ct);
        return Result.Success();
    }
}
