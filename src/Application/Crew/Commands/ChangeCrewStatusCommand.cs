using EventWOS.Application.Interfaces;
using EventWOS.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using MediatR;
using EventWOS.Shared.Result;

namespace EventWOS.Application.Crew.Commands;

/// <summary>
/// Suspend / reactivate a crew member. Mirrors ChangeVendorStatusCommand
/// exactly — same shape, same scoping approach (Role == Crew instead of
/// Role == Vendor) — so the "Suspend" button behaves identically for both
/// roster types instead of crew getting a second, subtly different code
/// path. Gated by crew:write, not a hard Admin-only check, so a Manager
/// with that permission can suspend crew the same way they can vendors.
/// </summary>
public sealed record ChangeCrewStatusCommand(
    Guid CrewId, string Status, Guid ActorId,
    // True when the caller is a Vendor (not Admin/Manager). A Vendor only
    // has crew:write over crew tied to them via VendorId — this command has
    // no other way to know that, since it only receives the actor's raw id.
    bool ActorIsVendor = false
) : IRequest<Result>;

public sealed class ChangeCrewStatusHandler : IRequestHandler<ChangeCrewStatusCommand, Result>
{
    private readonly IAppDbContext _db;
    public ChangeCrewStatusHandler(IAppDbContext db) => _db = db;

    public async Task<Result> Handle(ChangeCrewStatusCommand req, CancellationToken ct)
    {
        var crew = await _db.Users.FirstOrDefaultAsync(
            u => u.Id == req.CrewId && u.Role == UserRole.Crew && !u.IsDeleted, ct);
        if (crew is null) return Result.Failure(new Error("Crew.NotFound", "Crew member not found."));

        // A Vendor can only suspend/reactivate their own crew — GetCrewQuery
        // already scopes the list they see, but this command has no such
        // scoping of its own, so it must be enforced here too.
        if (req.ActorIsVendor && crew.VendorId != req.ActorId)
            return Result.Failure(new Error("Crew.NotYours", "You can only manage your own crew."));

        switch (req.Status.ToLower())
        {
            case "active":      crew.Reactivate(req.ActorId); break;
            case "suspended":   crew.Suspend(req.ActorId);    break;
            case "deactivated": crew.Deactivate(req.ActorId); break;
            default: return Result.Failure(new Error("Crew.InvalidStatus", "Invalid status value."));
        }

        await _db.SaveChangesAsync(ct);
        return Result.Success();
    }
}
