using EventWOS.Application.Interfaces;
using EventWOS.Application.Vendors.DTOs;
using EventWOS.Domain.Entities;
using EventWOS.Domain.Enums;
using EventWOS.Shared.Result;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EventWOS.Application.Crew.Commands;

public sealed record CreateCrewCommand(
    string Mobile, string FullName, string? Email, string? ReferralCode
) : IRequest<Result<CrewDto>>;

public sealed class CreateCrewHandler : IRequestHandler<CreateCrewCommand, Result<CrewDto>>
{
    private readonly IAppDbContext _db;
    public CreateCrewHandler(IAppDbContext db) => _db = db;

    public async Task<Result<CrewDto>> Handle(CreateCrewCommand req, CancellationToken ct)
    {
        if (await _db.Users.AnyAsync(u => u.Mobile == req.Mobile, ct))
            return Result.Failure<CrewDto>(new Error("Crew.DuplicateMobile", "Mobile already registered."));

        // A vendor is mandatory for every crew member created through this
        // endpoint (Vendor self-service always sends their own code; the
        // Admin/Manager "Add Crew" flow must select one from a dropdown of
        // active vendors — there is no such thing as an unassigned crew
        // member created this way). Crew self-registration is a separate
        // command (RegisterCrewCommand) and is unaffected by this rule.
        if (string.IsNullOrWhiteSpace(req.ReferralCode))
            return Result.Failure<CrewDto>(new Error("Crew.VendorRequired", "A vendor must be selected for this crew member."));

        var vendor = await _db.Users.FirstOrDefaultAsync(
            u => u.ReferralCode == req.ReferralCode && u.Role == UserRole.Vendor && !u.IsDeleted, ct);
        if (vendor is null)
            return Result.Failure<CrewDto>(new Error("Crew.InvalidReferral", "Invalid referral code."));

        var crew = new User(req.Mobile, req.FullName, UserRole.Crew);
        crew.Activate();
        if (req.Email is not null) crew.Email = req.Email;
        crew.JoinVendor(vendor.Id);

        _db.Users.Add(crew);
        await _db.SaveChangesAsync(ct);

        return Result.Success(MapToDto(crew, vendor.FullName));
    }

    internal static CrewDto MapToDto(User c, string? vendorName) => new(
        c.Id, c.Mobile, c.FullName, c.Email, c.AvatarUrl,
        c.Status.ToString(), c.VendorId, vendorName,
        c.DisciplineScore, c.EventsAttended, c.CreatedAt);
}
